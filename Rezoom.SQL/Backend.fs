﻿namespace Rezoom.SQL
open System
open System.Data
open System.Data.Common
open System.Collections.Generic
open Rezoom.SQL.Mapping
open Rezoom.SQL
open FSharp.Quotations

type IParameterIndexer =
    abstract member ParameterIndex : parameter : BindParameter -> int

type ParameterTransform =
    {   ParameterType : DbType
        ValueTransform : Quotations.Expr -> Quotations.Expr
    }
    static member Default(columnType : ColumnType) =
        let transform (expr : Quotations.Expr) =
            let ty = expr.Type
            let asObj = Expr.Coerce(expr, typeof<obj>)
            if ty.IsConstructedGenericType && ty.GetGenericTypeDefinition() = typedefof<_ option> then
                let invokeValue = Expr.Coerce(Expr.PropertyGet(expr, ty.GetProperty("Value")), typeof<obj>)
                <@@ if isNull %%asObj then box DBNull.Value else %%invokeValue @@>
            else
                <@@ if isNull %%asObj then box DBNull.Value else %%asObj @@>
        let ty = columnType.DbType
        {   ParameterType = ty
            ValueTransform = transform
        }

type IBackend =
    abstract member InitialModel : Model
    abstract member MigrationBackend : Quotations.Expr<DbConnection -> Migrations.IMigrationBackend>
    abstract member ParameterTransform
        : columnType : ColumnType -> ParameterTransform
    abstract member ToCommandFragments
        : indexer : IParameterIndexer * stmts : TTotalStmts -> CommandFragment IReadOnlyList
