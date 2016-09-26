﻿namespace Rezoom.ORM.SQLProvider
open System
open System.Collections.Generic
open Rezoom.ORM
open SQLow

type IParameterIndexer =
    abstract member ParameterIndex : parameter : BindParameter -> int

type IBackend =
    abstract member Builtin : DatabaseBuiltin
    abstract member MapPrimitiveType : ty : ColumnType -> Type
    abstract member ToCommandFragments
        : indexer : IParameterIndexer * stmts : TStmt IReadOnlyList -> CommandFragment IReadOnlyList
