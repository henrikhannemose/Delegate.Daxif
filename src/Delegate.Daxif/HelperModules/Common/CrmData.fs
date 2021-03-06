﻿namespace DG.Daxif.HelperModules.Common

open System
open Microsoft.Crm.Sdk
open Microsoft.Xrm.Sdk
open Microsoft.Xrm.Sdk.Client
open Microsoft.Xrm.Sdk.Messages
open Microsoft.Xrm.Sdk.Metadata
open Microsoft.Xrm.Sdk.Query
open DG.Daxif
open DG.Daxif.HelperModules.Common.Utility

// http://msdn.microsoft.com/en-us/library/microsoft.xrm.sdk.organizationrequest.requestname.aspx
module CrmData = 
  module Metadata = 
    let private entityHelper proxy (logicalName:string) filter = 
      let (proxy : OrganizationServiceProxy) = proxy
      let (filter : EntityFilters) = filter
      let req = OrganizationRequest()
      let param = ParameterCollection()
      param.Add(@"LogicalName", logicalName)
      param.Add(@"EntityFilters", filter)
      param.Add(@"MetadataId", Guid.Empty)
      param.Add(@"RetrieveAsIfPublished", true)
      req.RequestName <- @"RetrieveEntity"
      req.Parameters.AddRange(param)
      let resp = proxy.Execute(req)
      (Seq.head resp.Results).Value :?> EntityMetadata
    
    let entity proxy logicalName = 
      entityHelper proxy logicalName EntityFilters.Entity
    let entityAll proxy logicalName = 
      entityHelper proxy logicalName EntityFilters.All
    let entityAttributes proxy logicalName = 
      (entityHelper proxy logicalName EntityFilters.Attributes).Attributes
    let entityOneToManyRelationships proxy logicalName = 
      (entityHelper proxy logicalName EntityFilters.Relationships).OneToManyRelationships
    let entityManyToOneRelationships proxy logicalName = 
      (entityHelper proxy logicalName EntityFilters.Relationships).ManyToOneRelationships
    let entityManyToManyRelationships proxy logicalName = 
      (entityHelper proxy logicalName EntityFilters.Relationships).ManyToManyRelationships
    
    let allEntities (proxy : OrganizationServiceProxy) = 
      let req = OrganizationRequest()
      let param = ParameterCollection()
      param.Add(@"EntityFilters", EntityFilters.Entity)
      param.Add(@"RetrieveAsIfPublished", true)
      req.RequestName <- @"RetrieveAllEntities"
      req.Parameters.AddRange(param)
      proxy.Timeout <- new TimeSpan(0, 10, 0) // 10 minutes timeout
      let resp = proxy.Execute(req)
      (Seq.head resp.Results).Value :?> seq<EntityMetadata>
  
  module CRUD = 

    let version () = () // TODO:

    let createReq entity parameters = 
      let (parameters : ParameterCollection) = parameters
      let req = Messages.CreateRequest()
      req.Target <- entity
      req.Parameters.AddRange(parameters)
      req
    
    let create proxy entity parameters = 
      let (proxy : OrganizationServiceProxy) = proxy
      let (parameters : ParameterCollection) = parameters
      let req = createReq entity parameters
      let resp = proxy.Execute(req) :?> Messages.CreateResponse
      resp.id
    
    let retrieveReq logicalName guid = 
      let req = Messages.RetrieveRequest()
      req.ColumnSet <- ColumnSet(true)
      req.Target <- EntityReference(logicalName, id = guid)
      req
    
    let retrieve proxy logicalName guid = 
      let (proxy : OrganizationServiceProxy) = proxy
      let req = retrieveReq logicalName guid
      let resp = proxy.Execute(req) :?> Messages.RetrieveResponse
      resp.Entity
    
    let retrieveMultiple proxy logicalName query = 
      try 
        let (proxy : OrganizationServiceProxy) = proxy
        let (query : QueryExpression) = query
        query.PageInfo <- PagingInfo()
        query.PageInfo.ReturnTotalRecordCount <- true
        query.PageInfo.PageNumber <- 1
        query.PageInfo.Count <- 1000 // ItemsInEachCall 
        seq { 
          let resp = proxy.RetrieveMultiple(query)
          yield! resp.Entities
          let rec retrieveMultiple' (ec : EntityCollection) pn = 
            seq { 
              match ec.MoreRecords with
              | true -> 
                query.PageInfo.PageNumber <- (pn + 1)
                query.PageInfo.PagingCookie <- ec.PagingCookie
                proxy.Timeout <- new TimeSpan(0, 10, 0) // 10 minutes timeout
                let resp' = proxy.RetrieveMultiple(query)
                yield! resp'.Entities
                yield! retrieveMultiple' resp' (pn + 1)
              | false -> ()
            }
          yield! retrieveMultiple' resp 1
        }
      with ex -> 
        failwith 
          ("Retrieving " + logicalName + " failed: " 
           + Utility.getFullException (ex))
    
    let updateReq entity = 
      let req = Messages.UpdateRequest()
      req.Target <- entity
      req
    
    let update proxy entity = 
      let (proxy : OrganizationServiceProxy) = proxy
      let req = updateReq entity
      proxy.Execute(req) :?> Messages.UpdateResponse |> ignore
      entity.Id
    
    let deleteReq logicalName guid = 
      let req = Messages.DeleteRequest()
      req.Target <- EntityReference(logicalName, id = guid)
      req
    
    let delete proxy logicalName guid = 
      let (proxy : OrganizationServiceProxy) = proxy
      let req = deleteReq logicalName guid
      proxy.Execute(req) :?> Messages.DeleteResponse |> ignore
      Guid.Empty
    
    let publish proxy = 
      let (proxy : OrganizationServiceProxy) = proxy
      let req = Messages.PublishAllXmlRequest()
      proxy.Timeout <- new TimeSpan(1, 0, 0) // 1 hour timeout for PublishAll
      proxy.Execute(req) :?> Messages.PublishAllXmlResponse |> ignore

    let getResponse<'T when 'T :> OrganizationResponse> (proxy:OrganizationServiceProxy) request =
      proxy.Timeout <- TimeSpan(1,0,0)
      (proxy.Execute(request)) :?> 'T

    let performAsBulk proxy reqs = 
      reqs
      |> FSharpCoreExt.Array.chunk 1000
      |> Array.map (fun splitReqs ->
        let req = ExecuteMultipleRequest()
        req.Requests <- OrganizationRequestCollection()
        req.Requests.AddRange(splitReqs)
        req.Settings <- ExecuteMultipleSettings()
        req.Settings.ContinueOnError <- true
        req.Settings.ReturnResponses <- true
        (getResponse<ExecuteMultipleResponse> proxy req).Responses
      ) |> Seq.concat
      |> Array.ofSeq
