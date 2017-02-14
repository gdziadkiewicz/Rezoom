﻿namespace Rezoom.SQL.Compiler
open System
open System.Data
open System.Data.Common
open System.Collections.Generic

type DatabaseBuiltin =
    {   Functions : Map<Name, FunctionType>
    }

type Model =
    {   Schemas : Map<Name, Schema>
        DefaultSchema : Name
        TemporarySchema : Name
        Builtin : DatabaseBuiltin
    }
    member this.Schema(name : Name option) =
        this.Schemas |> Map.tryFind (name |? this.DefaultSchema)

and Schema =
    {   SchemaName : Name
        Objects : Map<Name, SchemaObject>
    }
    static member Empty(name) =
        {   SchemaName = name
            Objects = Map.empty
        }
    member this.ContainsObject(name : Name) = this.Objects.ContainsKey(name)

and SchemaObject =
    | SchemaTable of SchemaTable
    | SchemaView of SchemaView

and SchemaIndex =
    {   SchemaName : Name
        TableName : Name
        IndexName : Name
        Columns : Name Set
    }

and SchemaConstraint =
    {   SchemaName : Name
        TableName : Name
        ConstraintName : Name
        Columns : Name Set
    }

and SchemaTable =
    {   SchemaName : Name
        TableName : Name
        Columns : Map<Name, SchemaColumn>
        Indexes : Map<Name, SchemaIndex>
        Constraints : Map<Name, SchemaConstraint>
    }
    member this.WithAdditionalColumn(col : ColumnDef<_, _>) =
        match this.Columns |> Map.tryFind col.Name with
        | Some _ -> Error <| Error.columnAlreadyExists col.Name
        | None ->
            let isPrimaryKey =
                col.Constraints
                |> Seq.exists(
                    function | { ColumnConstraintType = PrimaryKeyConstraint _ } -> true | _ -> false)
            let newCol =
                {   SchemaName = this.SchemaName
                    TableName = this.TableName
                    PrimaryKey = isPrimaryKey
                    ColumnName = col.Name
                    ColumnType = ColumnType.OfTypeName(col.Type, col.Nullable)
                }
            Ok { this with Columns = this.Columns |> Map.add newCol.ColumnName newCol }
    static member OfCreateDefinition(schemaName, tableName, def : CreateTableDefinition<_, _>) =
        let tablePkColumns =
            seq {
                for constr in def.Constraints do
                    match constr.TableConstraintType with
                    | TableIndexConstraint { Type = PrimaryKey; IndexedColumns = indexed } ->
                        for name, _ in indexed -> name
                    | _ -> ()
            } |> Set.ofSeq
        let tableColumns =
            seq {
                for column in def.Columns ->
                    let isPrimaryKey =
                        tablePkColumns.Contains(column.Name)
                        || column.Constraints |> Seq.exists(function
                            | { ColumnConstraintType = PrimaryKeyConstraint _ } -> true
                            | _ -> false)
                    {   SchemaName = schemaName
                        TableName = tableName
                        PrimaryKey = isPrimaryKey
                        ColumnName = column.Name
                        ColumnType = ColumnType.OfTypeName(column.Type, column.Nullable)
                    }
            }
        {   SchemaName = schemaName
            TableName = tableName
            Columns = tableColumns |> mapBy (fun c -> c.ColumnName)
            Indexes = Map.empty
            Constraints =
                seq {
                    for constr, names in def.AllConstraints() ->
                        constr,
                            {   SchemaName = schemaName
                                TableName = tableName
                                ConstraintName = constr
                                Columns = names
                            }
                } |> Map.ofSeq
        }

and SchemaColumn =
    {   SchemaName : Name
        TableName : Name
        ColumnName : Name
        /// True if this column is part of the table's primary key.
        PrimaryKey : bool
        ColumnType : ColumnType
    }

and SchemaView =
    {   SchemaName : Name
        ViewName : Name
        CreateDefinition : CreateViewStmt
    }
    member this.Definition = this.CreateDefinition.AsSelect

and ExprInfo<'t> =
    {   /// The inferred type of this expression.
        Type : 't
        /// Does this expression return the same value each time it's run?
        Idempotent : bool
        /// If this expression is a function call, the function that it calls.
        Function : FunctionType option
        /// If this expression accesses a column of a table in the schema, the column's information.
        Column : SchemaColumn option
    }
    member this.PrimaryKey =
        match this.Column with
        | None -> false
        | Some c -> c.PrimaryKey
    static member OfType(t : 't) =
        {   Type = t
            Idempotent = true
            Function = None
            Column = None
        }
    member this.Map(f : 't -> _) =
        {   Type = f this.Type
            Idempotent = this.Idempotent
            Function = this.Function
            Column = this.Column
        }

and ColumnExprInfo<'t> =
    {   Expr : Expr<'t ObjectInfo, 't ExprInfo>
        FromAlias : Name option // table alias this was selected from, if any
        ColumnName : Name
    }
    member this.Map(f : 't -> _) =
        {   Expr =
                let mapping = ASTMapping<'t ObjectInfo, 't ExprInfo, _, _>((fun t -> t.Map(f)), fun e -> e.Map(f))
                mapping.Expr(this.Expr)
            FromAlias = this.FromAlias
            ColumnName = this.ColumnName
        }

and QueryExprInfo<'t> =
    { Columns : 't ColumnExprInfo IReadOnlyList }
    member this.Idempotent =
        this.Columns |> Seq.forall (fun e -> e.Expr.Info.Idempotent)
    member this.ColumnsWithNames(names) =
        let mine = this.Columns |> toDictionary (fun c -> c.ColumnName)
        let filtered =
            seq {
                for { WithSource.Source = source; Value = name } in names do
                    let succ, found = mine.TryGetValue(name)
                    if succ then yield found
                    else failAt source <| Error.noSuchColumn name
            } |> toReadOnlyList
        { Columns = filtered }
    member this.ColumnByName(name) =
        let matches =
            this.Columns
            |> Seq.filter (fun c -> c.ColumnName = name)
            |> Seq.truncate 2
            |> Seq.toList
        match matches with
        | [] -> NotFound <| Error.noSuchColumn name
        | [ single ] -> Found single
        | { FromAlias = Some a1 } :: { FromAlias = Some a2 } :: _ when a1 <> a2 ->
            Ambiguous <| Error.ambiguousColumnBetween name a1 a2
        | _ -> Ambiguous <| Error.ambiguousColumn name
    member this.RenameColumns(names : Name IReadOnlyList) =
        if names.Count <> this.Columns.Count then
            Error <| Error.mismatchedColumnNameCount names.Count this.Columns.Count
        else
            let newColumns =
                (this.Columns, names)
                ||> Seq.map2 (fun col newName -> { col with ColumnName = newName })
                |> toReadOnlyList
            Ok { Columns = newColumns }
    member this.Append(right : 't QueryExprInfo) =
        { Columns = appendLists this.Columns right.Columns }
    member this.Map(f : 't -> _) =
        { Columns = this.Columns |> Seq.map (fun c -> c.Map(f)) |> toReadOnlyList }

and TableReference =
    | TableReference of SchemaTable
    | ViewReference of SchemaView * TCreateViewStmt
    | CTEReference of Name
    | FromClauseReference of Name
    | SelectClauseReference of Name
    | SelectResults
    | CompoundTermResults

and TableLikeExprInfo<'t> =
    {   Table : TableReference
        Query : QueryExprInfo<'t>
    }
    member this.Map(f : 't -> _) =
        {   Table = this.Table
            Query = this.Query.Map(f)
        }

and ObjectInfo<'t> =
    | TableLike of 't TableLikeExprInfo
    | Index of SchemaIndex
    | Missing
    member this.Idempotent =
        match this with
        | TableLike t -> t.Query.Idempotent
        | Index _
        | Missing -> true
    member this.Table =
        match this with
        | TableLike t -> t
        | other -> bug <| sprintf "Bug: expected table, but found reference to %A" other
    member this.Query = this.Table.Query
    member this.Columns = this.Query.Columns
    member this.Map<'t1>(f : 't -> 't1) : ObjectInfo<'t1> =
        match this with
        | TableLike t -> TableLike (t.Map(f))
        | Index i -> Index i
        | Missing -> Missing


and TSelectStmt = SelectStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
and TCreateViewStmt = CreateViewStmt<ColumnType ObjectInfo, ColumnType ExprInfo>

type TExprType = ExprType<ColumnType ObjectInfo, ColumnType ExprInfo>
type TExpr = Expr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TInExpr = InExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCollationExpr = CollationExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TBetweenExpr = BetweenExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TSimilarityExpr = SimilarityExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TBinaryExpr = BinaryExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TUnaryExpr = UnaryExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TObjectName = ObjectName<ColumnType ObjectInfo>
type TColumnName = ColumnName<ColumnType ObjectInfo>
type TInSet = InSet<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCaseExpr = CaseExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCastExpr = CastExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TFunctionInvocationExpr = FunctionInvocationExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
    
type TWithClause = WithClause<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCommonTableExpression = CommonTableExpression<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCompoundExprCore = CompoundExprCore<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCompoundExpr = CompoundExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCompoundTermCore = CompoundTermCore<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCompoundTerm = CompoundTerm<ColumnType ObjectInfo, ColumnType ExprInfo>
type TForeignKeyClause = ForeignKeyClause<ColumnType ObjectInfo>
type TCreateTableDefinition = CreateTableDefinition<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCreateTableStmt = CreateTableStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TSelectCore = SelectCore<ColumnType ObjectInfo, ColumnType ExprInfo>
type TJoinConstraint = JoinConstraint<ColumnType ObjectInfo, ColumnType ExprInfo>
type TJoin = Join<ColumnType ObjectInfo, ColumnType ExprInfo>
type TLimit = Limit<ColumnType ObjectInfo, ColumnType ExprInfo>
type TGroupBy = GroupBy<ColumnType ObjectInfo, ColumnType ExprInfo>
type TOrderingTerm = OrderingTerm<ColumnType ObjectInfo, ColumnType ExprInfo>
type TResultColumn = ResultColumn<ColumnType ObjectInfo, ColumnType ExprInfo>
type TResultColumns = ResultColumns<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTableOrSubquery = TableOrSubquery<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTableExprCore = TableExprCore<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTableExpr = TableExpr<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTableInvocation = TableInvocation<ColumnType ObjectInfo, ColumnType ExprInfo>

type TColumnConstraint = ColumnConstraint<ColumnType ObjectInfo, ColumnType ExprInfo>
type TColumnDef = ColumnDef<ColumnType ObjectInfo, ColumnType ExprInfo>
type TAlterTableStmt = AlterTableStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TAlterTableAlteration = AlterTableAlteration<ColumnType ObjectInfo, ColumnType ExprInfo>
type TCreateIndexStmt = CreateIndexStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTableIndexConstraintClause = TableIndexConstraintClause<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTableConstraint = TableConstraint<ColumnType ObjectInfo, ColumnType ExprInfo>
type TQualifiedTableName = QualifiedTableName<ColumnType ObjectInfo>
type TDeleteStmt = DeleteStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TDropObjectStmt = DropObjectStmt<ColumnType ObjectInfo>
type TUpdateStmt = UpdateStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TInsertStmt = InsertStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TStmt = Stmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TVendorStmt = VendorStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTotalStmt = TotalStmt<ColumnType ObjectInfo, ColumnType ExprInfo>
type TTotalStmts = TTotalStmt IReadOnlyList