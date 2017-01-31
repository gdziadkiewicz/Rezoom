﻿namespace Rezoom.SQL.TSQL
open System
open System.Data
open System.Data.Common
open System.Collections.Generic
open System.Globalization
open Rezoom.SQL
open Rezoom.SQL.Mapping
open Rezoom.SQL.BackendUtilities
open Rezoom.SQL.Translators

module private TSQLFunctions =
    open Rezoom.SQL.FunctionDeclarations
    type CustomTranslator = ExprTranslator -> TFunctionInvocationExpr -> Fragments
    let private noArgProc name ret =
        proc name [] ret, Some <| fun _ _ -> [| text <| name.ToUpperInvariant() |] :> _ seq
    let private atAtProc name ret =
        proc name [] ret, Some <| fun _ _ -> [| text <| "@@" + name.ToUpperInvariant() |] :> _ seq
    let private datePartWhitelist =
        [|  "year"; "yy"; "yyyy"
            "quarter"; "qq"; "q"
            "month"; "mm"; "m"
            "dayofyear"; "dy"; "y"
            "day"; "dd"; "d"
            "week"; "wk"; "ww"
            "weekday"; "dw"
            "hour"; "hh"
            "minute"; "mi"; "n"
            "second"; "ss"; "s"
            "millisecond"; "ms"
            "microsecond"; "mcs"
            "nanosecond"; "ns"
            "tzoffset"; "tz"
            "iso_week"; "isowk"; "isoww"
        |] |> fun arr -> HashSet(arr, StringComparer.OrdinalIgnoreCase)
    let private datePartFunc name otherArgs ret =
        func name (string :: otherArgs) ret,
            Some <| fun (stmt : ExprTranslator) (invoc : TFunctionInvocationExpr) ->
                seq {
                    yield text invoc.FunctionName.Value
                    yield text "("
                    match invoc.Arguments with
                    | ArgumentList (None, args) when args.Length > 0 ->
                        match args.[0] with
                        | { Value = LiteralExpr (StringLiteral lit) } ->
                            if datePartWhitelist.Contains(lit) then
                                yield text lit
                            else
                                failAt args.[0].Source <|
                                    sprintf "DATEPART argument must be one of %A" (List.ofSeq datePartWhitelist)
                        | _ ->
                            failAt args.[0].Source "DATEPART argument must be a string literal"
                        for i = 1 to args.Length - 1 do
                            yield text ","
                            yield! stmt.Expr(args.[i], FirstClassValue)
                    | _ -> bug "Can't use datePartFunc with no args"
                    yield text ")"
                }
    let private aggregate name args ret = aggregate name args ret, None
    let private aggregateW name args ret = aggregateW name args ret, None
    let private func name args ret = func name args ret, None
    let private proc name args ret = proc name args ret, None
    let private i = integral
    let private ii = infect i
    let private date = datetime
    let private specialFunctions = Dictionary()
    let private addCustom (funcType : FunctionType, custom) =
        match custom with
        | None -> funcType
        | Some custom ->
            specialFunctions.[funcType.FunctionName] <- custom
            funcType
    let getCustom (funcName : Name) =
        let succ, value = specialFunctions.TryGetValue(funcName)
        if succ then Some value else None
    let functions =
        [|  // aggregate functions
            aggregate "avg" [ numeric a' ] (nullable a')
            aggregateW "count" [ scalar ] int32
            aggregateW "count_big" [ scalar ] int64
            aggregate "grouping" [ scalar ] int8
            aggregate "grouping_id" [ vararg scalar ] int32
            aggregate "max" [ a' ] (nullable a')
            aggregate "min" [ a' ] (nullable a')
            aggregate "sum" [ numeric a' ] a'
            aggregate "stdev" [ numeric scalar ] (nullable float64)
            aggregate "stdevp" [ numeric scalar ] (nullable float64)
            aggregate "var" [ numeric scalar ] (nullable float64)
            aggregate "varp" [ numeric scalar ] (nullable float64)
            // @@FUNCTIONNAME builtins
            atAtProc "datefirst" int8
            atAtProc "dbts" binary
            atAtProc "langid" int8
            atAtProc "language" string
            atAtProc "lock_timeout" int32
            atAtProc "max_connections" int32
            atAtProc "max_precision" int8
            atAtProc "nestlevel" int32
            atAtProc "options" int32
            atAtProc "remserver" string
            atAtProc "servername" string
            atAtProc "servicename" string
            atAtProc "spid" int8
            atAtProc "textsize" int32
            atAtProc "version" string
            atAtProc "cursor_rows" int32
            atAtProc "fetch_status" int32
            atAtProc "identity" i
            // identity
            proc "scope_identity" [] i
            // date/time functions from https://msdn.microsoft.com/en-us/library/ms186724.aspx
            noArgProc "current_timestamp" datetime
            proc "sysdatetime" [] datetime
            proc "sysdatetimeoffset" [] datetimeoffset
            proc "sysutcdatetime" [] datetime
            proc "getdate" [] datetime
            proc "getutcdate" [] datetime
            datePartFunc "datename" [ infect datetime ] string
            datePartFunc "dateadd" [ infect datetime ] string
            datePartFunc "datediff" [ infect datetime; infect datetime ] int32
            datePartFunc "datediff_big" [ infect datetime; infect datetime ] int64
            datePartFunc "dateadd" [ infect i; infect datetime ] datetime
            func "day" [ infect datetime ] i
            func "month" [ infect datetime ] i
            func "year" [ infect datetime ] i
            func "datefromparts" [ ii; ii; ii ] date
            func "datetime2fromparts" [ ii; ii; ii; ii; ii; ii; ii; ii ] datetime
            func "datetimefromparts" [ ii; ii; ii; ii; ii; ii; ii ] datetime
            func "datetimeoffsetfromparts" [ ii; ii; ii; ii; ii; ii; ii; ii; ii; ii ] datetimeoffset
            func "smalldatetimefromparts" [ ii; ii; ii; ii; ii ] datetime
            // math funcs from https://msdn.microsoft.com/en-us/library/ms177516.aspx
            func "acos" [ infect fractional ] float64
            func "asin" [ infect fractional ] float64
            func "atan" [ infect fractional ] float64
            func "atn2" [ infect fractional; infect fractional ] float64
            func "ceiling" [ infect (numeric a') ] a'
            func "cos" [ infect fractional] float64
            func "cot" [ infect fractional ] float64
            func "degrees" [ infect (numeric a') ] a'
            func "exp" [ infect fractional ] float64
            func "floor" [ infect (numeric a') ] a'
            func "log" [ infect num; infect (optional i) ] float64
            func "log10" [ infect num ] float64
            func "pi" [] float64
            func "power" [ infect (numeric a'); infect num ] a'
            func "radians" [ infect (numeric a') ] a'
            func "rand" [ infect (optional i) ] float64
            func "round" [ infect (numeric a'); infect i ] a'
            func "sign" [ infect (numeric a') ] a'
            func "sin" [ infect fractional ] float64
            func "sqrt" [ infect (numeric a') ] float64
            func "square" [ infect (numeric a') ] float64
            func "tan" [ infect fractional ] float64
            // JSON functions from https://msdn.microsoft.com/en-us/library/dn921900.aspx
            func "isjson" [ infect string ] boolean
            func "json_value" [ infect string; infect string ] string
            func "json_query" [ infect string; infect string ] string
            func "json_modify" [ infect string; infect string; infect string ] string
            // logical funcs from https://msdn.microsoft.com/en-us/library/hh213226.aspx
            func "choose" [ infect i; vararg (infect a') ] a'
            func "iif" [ boolean; infect a'; infect a' ] a'
            // skip over "metadata functions" (for now) from https://msdn.microsoft.com/en-us/library/ms187812.aspx
            // ...
            // also "security functions" (for now) from https://msdn.microsoft.com/en-us/library/ms186236.aspx
            // ...
            // so onto string functions from https://msdn.microsoft.com/en-us/library/ms181984.aspx
            func "ascii" [ infect string ] int32
            func "concat" [ string; string; vararg string ] string
            func "format" [ infect scalar; infect string; optional (infect string) ] string
            func "lower" [ infect string ] string
            func "upper" [ infect string ] string
            func "patindex" [ infect string; infect string ] integral
            func "replicate" [ infect string; infect integral ] string
            func "rtrim" [ infect string ] string
            func "ltrim" [ infect string ] string
            func "str" [ infect fractional; varargN 2 integral ] string
            // func "string_split" [ infect string; infect string ] string_table // wait till we can do TVFs
            func "translate" [ infect string; infect string; infect string ] string
            func "char" [ infect integral ] string
            func "concat_ws" [ infect string; scalar; scalar; vararg scalar ] string
            func "left" [ infect string; infect integral ] string
            func "right" [ infect string; infect integral ] string
            func "quotename" [ infect string; optional (infect string) ] string
            func "reverse" [ infect string ] string
            func "soundex" [ infect string ] string
            // func "string_agg" // wtf, how do we support this? it has its own special clause type...
            func "stuff" [ infect (a' |> constrained StringishTypeClass); infect integral; infect integral; string ] a'
            func "trim" [ infect string ] string // come on TSQL, "characters from"? cut it out...
            func "charindex" [ infect string; infect string ; optional integral ] integral
            func "difference" [ infect string; infect string ] int32
            func "len" [ infect string ] integral
            func "nchar" [ infect integral ] string
            func "replace" [ infect string; infect string; infect string ] string
            func "space" [ infect integral ] string
            func "string_escape" [ infect string; infect string ] string // TODO: enforce literal on 2nd arg?
            func "substring" [ infect a' |> constrained StringishTypeClass; infect integral; infect integral ] a'
            func "unicode" [ infect string ] int32
            // missing: system functions, system statistical functions, text and image functions
        |] |> Array.map addCustom |> DefaultFunctions.extendedBy

type private TSQLLiteral() =
    inherit DefaultLiteralTranslator()
    override __.BooleanLiteral(t) = CommandText <| if t then "1" else "0"
    override __.BlobLiteral(bytes) =
        let hexPairs = bytes |> Array.map (fun b -> b.ToString("X2", CultureInfo.InvariantCulture))
        "0x" + String.Concat(hexPairs) |> text

type private TSQLExpression(statement : StatementTranslator, indexer) =
    inherit DefaultExprTranslator(statement, indexer)
    let literal = DefaultLiteralTranslator()
    override __.Literal = upcast literal
    override __.Name(name) =
        "[" + name.Value.Replace("]", "]]") + "]"
        |> text
    override __.TypeName(name) =
        (Seq.singleton << text) <|
            match name with
            | BooleanTypeName -> "BIT"
            | IntegerTypeName Integer8 -> "TINYINT"
            | IntegerTypeName Integer16 -> "SMALLINT"
            | IntegerTypeName Integer32 -> "INT"
            | IntegerTypeName Integer64 -> "BIGINT"
            | FloatTypeName Float32 -> "FLOAT(24)"
            | FloatTypeName Float64 -> "FLOAT(53)"
            | StringTypeName(len) -> "NVARCHAR(" + string len + ")"
            | BinaryTypeName(len) -> "VARBINARY(" + string len + ")"
            | DecimalTypeName -> "NUMERIC(38, 19)"
            | DateTimeTypeName -> "DATETIME2"
            | DateTimeOffsetTypeName -> "DATETIMEOFFSET"
    override __.BinaryOperator(op) =
        CommandText <|
        match op with
        | Concatenate -> "+"
        | Multiply -> "*"
        | Divide -> "/"
        | Modulo -> "%"
        | Add -> "+"
        | Subtract -> "-"
        | BitAnd -> "&"
        | BitOr -> "|"
        | LessThan -> "<"
        | LessThanOrEqual -> "<="
        | GreaterThan -> ">"
        | GreaterThanOrEqual -> ">="
        | Equal -> "="
        | NotEqual -> "<>"
        | And -> "AND"
        | Or -> "OR"
        | Is
        | IsNot -> bug "should have been handled for TSQL before we got here"
        | BitShiftLeft
        | BitShiftRight -> failwithf "Not supported by TSQL: %A" op
    override this.Binary(bin) =
        match bin.Operator, bin.Right.Value with
        | Is, LiteralExpr NullLiteral
        | IsNot, LiteralExpr NullLiteral ->
            seq {
                yield! this.Expr(bin.Left, FirstClassValue)
                yield ws
                yield text "IS"
                yield ws
                if bin.Operator = IsNot then
                    yield text "NOT"
                    yield ws
                yield text "NULL"
            }
        | Is, _
        | IsNot, _ ->
            seq {
                if bin.Operator = IsNot then
                    yield text "NOT"
                    yield ws
                yield text "EXISTS(SELECT"
                yield ws
                yield! this.Expr(bin.Left, FirstClassValue)
                yield ws
                yield text "INTERSECT SELECT"
                yield ws
                yield! this.Expr(bin.Right, FirstClassValue)
                yield text ")"
            }
        | _ -> base.Binary(bin)
    override __.UnaryOperator(op) =
        CommandText <|
        match op with
        | Negative -> "-"
        | Not -> "NOT"
        | NotNull -> "IS NOT NULL"
        | IsNull -> "IS NULL"
        | BitNot -> "~"
    override __.SimilarityOperator(op) =
        CommandText <|
        match op with
        | Like -> "LIKE"
        | Glob
        | Match
        | Regexp -> failwithf "Not supported by TSQL: %A" op
    /// Identifies expressions that are set up to use as predicates in T-SQL.
    /// These expressions don't produce actual values.
    /// For example, you can't `SELECT 1=1`, but you can do `SELECT 1 WHERE 1=1`.
    /// Conversely, you can't `SELECT 1 WHERE tbl.BitColumn`, but you can do `SELECT tbl.BitColumn`.
    static member private IsPredicateBoolean(expr : TExpr) =
        expr.Info.Type.Type = BooleanType
        &&  match expr.Value with
            | SimilarityExpr _
            | BetweenExpr _
            | InExpr _
            | ExistsExpr _
            | BinaryExpr _
            | UnaryExpr _ -> true
            | _ -> false
    member private __.BaseExpr(expr, context) = base.Expr(expr, context)
    override this.Expr(expr, context) =
        match context with
        | FirstClassValue ->
            if TSQLExpression.IsPredicateBoolean(expr) then
                seq {
                    yield text "CAST((CASE WHEN"
                    yield ws
                    yield! this.BaseExpr(expr, Predicate)
                    yield ws
                    yield text "THEN 1 ELSE 0 END) AS BIT)"
                }
            else
                base.Expr(expr, context)
        | Predicate ->
            if TSQLExpression.IsPredicateBoolean(expr) then
                base.Expr(expr, context)
            else
                seq {
                    yield text "(("
                    yield! this.BaseExpr(expr, FirstClassValue)
                    yield text ")<>0)"
                }
    override this.Invoke(func) =
        match TSQLFunctions.getCustom func.FunctionName with
        | Some custom -> custom (this :> ExprTranslator) func
        | None -> base.Invoke(func)

type private TSQLStatement(indexer : IParameterIndexer) as this =
    inherit DefaultStatementTranslator(Name("TSQL"), indexer)
    let expr = TSQLExpression(this :> StatementTranslator, indexer)
    override __.Expr = upcast expr
    override __.ColumnsNullableByDefault = true
    member this.SelectCoreWithTop(select : TSelectCore, top) =
        seq {
            yield text "SELECT"
            yield ws
            match top with
            | None -> ()
            | Some top ->
                yield text "TOP"
                yield ws
                yield! this.FirstClassValue(top)
                yield ws
            yield! this.ResultColumns(select.Columns)
            match select.From with
            | None -> ()
            | Some from ->
                yield ws
                yield text "FROM"
                yield ws
                yield! this.TableExpr(from)
            match select.Where with
            | None -> ()
            | Some where ->
                yield ws
                yield text "WHERE"
                yield ws
                yield! this.Predicate(where)
            match select.GroupBy with
            | None -> ()
            | Some groupBy ->
                yield ws
                yield text "GROUP BY"
                yield ws
                yield! groupBy.By |> Seq.map this.FirstClassValue |> join ","
                match groupBy.Having with
                | None -> ()
                | Some having ->
                    yield ws
                    yield text "HAVING"
                    yield ws
                    yield! this.Predicate(having)
        }
    override this.SelectCore(select) = this.SelectCoreWithTop(select, None)
    override this.Select(select) =
        match select.Value.Limit with
        | None -> base.Select(select)
        | Some limit ->
            // TSQL doesn't exactly support LIMIT so what shall we do?
            match limit.Offset, select.Value.Compound.Value with
            | None, CompoundTerm { Value = Select core } ->
                // We can use TOP here
                this.SelectCoreWithTop(core, Some limit.Limit)
            | _ ->
                this.Select(select) // Our override of LIMIT will turn this into an offset/fetch clause
    override this.Limit(limit) =
        seq {
            yield text "OFFSET"
            yield ws
            match limit.Offset with
            | Some offset ->
                yield! this.FirstClassValue(offset)
            | None ->
                yield text "0"
            yield ws
            yield text "ROWS FETCH NEXT"
            yield ws
            yield! this.FirstClassValue(limit.Limit)
            yield ws
            yield text "ROWS ONLY"
        }
    override this.AutoIncrement = "IDENTITY(1,1)"

type TSQLMigrationBackend(conn : DbConnection) =
    inherit DefaultMigrationBackend(conn)
    override __.Initialize() =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            """
                IF NOT EXISTS (
                    SELECT * FROM sys.tables t
                    JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = 'dbo' and t.name = '__RZSQL_MIGRATIONS'
                )
                CREATE TABLE __RZSQL_MIGRATIONS
                    ( MajorVersion int
                    , Name varchar(256)
                    , UNIQUE (MajorVersion, Name)
                    );
            """
        ignore <| cmd.ExecuteNonQuery()

type TSQLBackend() =
    static let initialModel =
        let main, temp = Name("dbo"), Name("temp")
        {   Schemas =
                [   Schema.Empty(main)
                    Schema.Empty(temp)
                ] |> List.map (fun s -> s.SchemaName, s) |> Map.ofList
            DefaultSchema = main
            TemporarySchema = temp
            Builtin =
                {   Functions = TSQLFunctions.functions
                }
        }
    interface IBackend with
        member this.MigrationBackend = <@ fun conn -> TSQLMigrationBackend(conn) :> Migrations.IMigrationBackend @>
        member this.InitialModel = initialModel
        member this.ParameterTransform(columnType) = ParameterTransform.Default(columnType)
        member this.ToCommandFragments(indexer, stmts) =
            let translator = TSQLStatement(indexer)
            translator.TotalStatements(stmts)
            |> BackendUtilities.simplifyFragments
            |> ResizeArray
            :> _ IReadOnlyList
       