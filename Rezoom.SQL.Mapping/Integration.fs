﻿[<AutoOpen>]
module Rezoom.SQL.Mapping.Integration
open System
open System.Configuration
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Threading
open Rezoom

[<AbstractClass>]
type ConnectionProvider() =
    abstract member Open : name : string -> DbConnection
    abstract member BeginTransaction : DbConnection -> DbTransaction
    default __.BeginTransaction(conn) = conn.BeginTransaction()

type DefaultConnectionProvider() =
    inherit ConnectionProvider()
    override __.Open(name) =
        let connectionStrings = ConfigurationManager.ConnectionStrings
        if isNull connectionStrings then
            failwith "No <connectionStrings> element in config"
        let connectionString = connectionStrings.[name]
        if isNull connectionString then
            failwith "No connection string by the expected name"
        let provider = DbProviderFactories.GetFactory(connectionString.ProviderName)
        let conn = provider.CreateConnection()
        conn.ConnectionString <- connectionString.ConnectionString
        conn.Open()
        conn

type private ExecutionLocalConnections(provider : ConnectionProvider) =
    let connections = Dictionary()
    member __.GetConnection(name) =
        let succ, tuple = connections.TryGetValue(name)
        if succ then fst tuple else
        let conn = provider.Open(name)
        let tran = provider.BeginTransaction(conn)
        connections.Add(name, (conn, tran))
        conn
    member __.Dispose(state) = 
        let mutable exn = null
        for conn, tran in connections.Values do
            try
                match state with
                | ExecutionSuccess -> tran.Commit()
                | ExecutionFault -> // don't explicitly rollback, tran.Dispose() should handle it
                try
                    tran.Dispose()
                finally
                    conn.Dispose()
            with
            | e ->
                if isNull exn then exn <- e
                else exn <- AggregateException(exn, e)
        connections.Clear()
        if not (isNull exn) then raise exn
    // don't implement IDisposable because we need exec. state to know how to end transactions

type private ExecutionLocalConnectionsFactory() =
    inherit ServiceFactory<ExecutionLocalConnections>()
    override __.ServiceLifetime = ServiceLifetime.ExecutionLocal
    override __.CreateService(cxt) =
        let provider = 
            match cxt.Configuration.TryGetConfig<ConnectionProvider>() with
            | None -> DefaultConnectionProvider() :> ConnectionProvider
            | Some provider -> provider
        ExecutionLocalConnections(provider)
    override __.DisposeService(state, svc) = svc.Dispose(state)

type private StepLocalBatches(conns : ExecutionLocalConnections) =
    let batches = Dictionary()
    member __.GetBatch(name) =
        let succ, batch = batches.TryGetValue(name)
        if succ then batch else
        let conn = conns.GetConnection(name)
        let batch = CommandBatch(conn)
        batches.Add(name, batch)
        batch

type private StepLocalBatchesFactory() =
    inherit ServiceFactory<StepLocalBatches>()
    override __.ServiceLifetime = ServiceLifetime.StepLocal
    override __.CreateService(cxt) = StepLocalBatches(cxt.GetService<ExecutionLocalConnectionsFactory, _>())
    override __.DisposeService(_, _) = ()

type private CommandErrandArgument(parameters : (obj * DbType) IReadOnlyList) =
    member __.Parameters = parameters
    member __.Equals(other : CommandErrandArgument) =
        Seq.forall2 (=) parameters other.Parameters
    override __.GetHashCode() =
        let mutable h = 0
        for par, _ in parameters do
            h <- ((h <<< 5) + h) ^^^ par.GetHashCode()
        h
    override this.Equals(other : obj) =
        match other with
        | :? CommandErrandArgument as other -> this.Equals(other)
        | _ -> false

type CommandErrand<'a>(command : Command<'a>) =
    inherit AsynchronousErrand<'a>()
    let cacheArgument = CommandErrandArgument(command.Parameters)
    override __.CacheInfo = command.CacheInfo
    override __.CacheArgument = box cacheArgument 
    override __.SequenceGroup = null
    override __.Prepare(cxt) =
        let batches = cxt.GetService<StepLocalBatchesFactory, _>()
        batches.GetBatch(command.ConnectionName).Batch(command)

type Command<'a> with
    member this.ExecutePlan() =
        CommandErrand(this) |> Plan.ofErrand
    member this.ExecuteAsync(conn : DbConnection) =
        CommandBatch(conn).Batch(this)(CancellationToken())
