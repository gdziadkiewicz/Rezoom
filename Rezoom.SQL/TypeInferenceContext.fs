﻿namespace Rezoom.SQL
open System
open System.Collections.Generic
open Rezoom.SQL
open Rezoom.SQL.InferredTypes

type private TypeInferenceVariable(id : TypeVariableId) =
    let inferredType =
        {   InferredType = TypeVariable id
            InferredNullable = NullableVariable id
        }
    let mutable currentNullable = NullableUnknown
    let mutable currentType = AnyTypeClass
    member __.Id = id
    member __.InferredType = inferredType
    member __.CurrentNullable = currentNullable
    member __.CurrentType = currentType
    member __.Unify(source, core : CoreColumnType) =
        let unified = currentType.Unify(core) |> resultAt source
        currentType <- unified
    member __.ForceNullable() =
        currentNullable <- NullableKnown true

type private TypeInferenceContext() =
    let variablesByParameter = Dictionary<BindParameter, TypeVariableId>()
    let variablesById = Dictionary<TypeVariableId, TypeInferenceVariable>()
    let deferredNullables = ResizeArray()
    let mutable nextVariableId = 0
    let getVar id =
        let succ, inferred = variablesById.TryGetValue(id)
        if not succ then bug "Type variable not found"
        else inferred
    static member UnifyColumnTypes(left : ColumnType, right : ColumnType) =
        result {
            let nullable = max left.Nullable right.Nullable
            let! ty = left.Type.Unify(right.Type)
            return { Nullable = nullable; Type = ty }
        }
    member this.NextVariable() =
        let id = nextVariableId
        nextVariableId <- nextVariableId + 1
        let var = TypeInferenceVariable(id)
        variablesById.Add(id, var)
        var
    member this.AnonymousVariable() =
        this.NextVariable().InferredType
    member private this.Variable(bindParameter) =
        let succ, v = variablesByParameter.TryGetValue(bindParameter)
        if succ then getVar v else
        let var = this.NextVariable()
        variablesByParameter.[bindParameter] <- var.Id
        var
    member this.Unify(source, left, right) =
        match left.InferredType, right.InferredType with
        | TypeKnown lk, TypeKnown rk ->
            {   InferredType = lk.Unify(rk) |> resultAt source |> TypeKnown
                InferredNullable = InferredNullable.Either(left.InferredNullable, right.InferredNullable)
            }
        | TypeVariable varId, TypeKnown knownType
        | TypeKnown knownType, TypeVariable varId ->
            (getVar varId).Unify(source, knownType)
            {   InferredType = TypeVariable varId
                InferredNullable = NullableVariable varId
            }
        | TypeVariable leftId, TypeVariable rightId ->
            let left, right = getVar leftId, getVar rightId
            left.Unify(source, right.CurrentType)
            variablesById.[rightId] <- left
            {   InferredType = TypeVariable leftId
                InferredNullable = NullableVariable leftId
            }
    member this.UnifyList(source, elem, list) =
        let var = this.Variable(list)
        match elem with
        | TypeVariable varId ->
            var.Unify(source, ListType (getVar varId).CurrentType)
        | TypeKnown knownType ->
            var.Unify(source, ListType knownType)
    member this.ForceNullable(source, nullable : InferredNullable) =
        match nullable.Simplify() with
        | NullableDueToJoin _
        | NullableUnknown
        | NullableKnown true -> ()
        | NullableKnown false ->
            failAt source Error.exprMustBeNullable
        | NullableVariable id -> (getVar id).ForceNullable()
        | NullableEither _ ->
            let rec allVars v =
                match v with
                | NullableUnknown
                | NullableKnown true 
                | NullableKnown false
                | NullableDueToJoin _ -> Seq.empty
                | NullableVariable id -> Seq.singleton id
                | NullableEither (l, r) -> Seq.append (allVars l) (allVars r)
            deferredNullables.Add(ResizeArray(allVars nullable))
    member this.ResolveNullable(nullable) =
        if deferredNullables.Count > 0 then
            let triviallySatisfied r =
                let t = NullableKnown true
                r |> Seq.exists (fun v -> (getVar v).CurrentNullable = t)
            ignore <| deferredNullables.RemoveAll(fun r -> triviallySatisfied r) // remove trivially satisfied reqs
            for vs in deferredNullables do // remaining vars must all be forced null
                for v in vs do
                    (getVar v).ForceNullable()
            deferredNullables.Clear()
        match nullable with
        | NullableUnknown -> false
        | NullableDueToJoin _ -> true
        | NullableKnown t -> t
        | NullableVariable id -> this.ResolveNullable((getVar id).CurrentNullable)
        | NullableEither (l, r) -> this.ResolveNullable(l) || this.ResolveNullable(r)
    member this.Concrete(inferred) =
        {   Nullable = this.ResolveNullable(inferred.InferredNullable)
            Type =
                match inferred.InferredType with
                | TypeKnown t -> t
                | TypeVariable id -> (getVar id).CurrentType
        }
    interface ITypeInferenceContext with
        member this.AnonymousVariable() = this.AnonymousVariable()
        member this.Variable(parameter) = this.Variable(parameter).InferredType
        member this.UnifyList(source, elem, list) = this.UnifyList(source, elem.InferredType, list)
        member this.Unify(source, left, right) = this.Unify(source, left, right)
        member this.ForceNullable(source, nullable) = this.ForceNullable(source, nullable)
        member this.Concrete(inferred) = this.Concrete(inferred)
        member __.Parameters = variablesByParameter.Keys :> _ seq
 
[<AutoOpen>]
module private TypeInferenceExtensions =
    type ITypeInferenceContext with
        member typeInference.Unify(source : SourceInfo, inferredType, coreType : CoreColumnType) =
            typeInference.Unify(source, inferredType, InferredType.Dependent(inferredType, coreType))
        member typeInference.Unify(source : SourceInfo, types : InferredType seq) =
            types
            |> Seq.fold
                (fun s next -> typeInference.Unify(source, s, next))
                InferredType.Scalar
        member typeInference.Concrete(inferred) = typeInference.Concrete(inferred)
        member typeInference.Binary(source, op, left, right) =
            match op with
            | Concatenate -> typeInference.Unify(source, [ left; right; InferredType.String ])
            | Multiply
            | Divide
            | Add
            | Subtract -> typeInference.Unify(source, [ left; right; InferredType.Number ])
            | Modulo
            | BitShiftLeft
            | BitShiftRight
            | BitAnd
            | BitOr -> typeInference.Unify(source, [ left; right; InferredType.Integer ])
            | LessThan
            | LessThanOrEqual
            | GreaterThan
            | GreaterThanOrEqual
            | Equal
            | NotEqual ->
                let operandType = typeInference.Unify(source, left, right)
                InferredType.Dependent(operandType, BooleanType)
            | Is
            | IsNot ->
                let operandType = typeInference.Unify(source, left, right)
                typeInference.ForceNullable(source, left.InferredNullable)
                typeInference.ForceNullable(source, right.InferredNullable)
                InferredType.Dependent(operandType, BooleanType)
            | And
            | Or -> typeInference.Unify(source, [ left; right; InferredType.Boolean ])
        member typeInference.Unary(source, op, operandType) =
            match op with
            | Negative
            | BitNot -> typeInference.Unify(source, operandType, InferredType.Number)
            | Not -> typeInference.Unify(source, operandType, InferredType.Boolean)
            | IsNull
            | NotNull ->
                typeInference.ForceNullable(source, operandType.InferredNullable)
                InferredType.Boolean
        member typeInference.AnonymousQueryInfo(columnNames) =
            {   Columns =
                    seq {
                        for { WithSource.Source = source; Value = name } in columnNames ->
                            {   ColumnName = name
                                FromAlias = None
                                Expr =
                                    {   Value = ColumnNameExpr { Table = None; ColumnName = name }
                                        Source = source
                                        Info = ExprInfo.OfType(typeInference.AnonymousVariable())
                                    }
                            }
                    } |> toReadOnlyList
            }
        member typeInference.Function(source : SourceInfo, func : FunctionType, invoc : InfFunctionArguments) =
            let functionVars = Dictionary()
            let aggregate = func.Aggregate(invoc)
            let term (termType : FunctionTermType) =
                match termType.TypeVariable with
                | None ->
                    {   InferredNullable = NullableKnown termType.ForceNullable
                        InferredType = TypeKnown termType.TypeConstraint
                    }
                | Some name ->
                    let succ, tvar = functionVars.TryGetValue(name)
                    let tvar =
                        if succ then tvar else
                        let avar = typeInference.AnonymousVariable()
                        functionVars.[name] <- avar
                        avar
                    let tvar = typeInference.Unify(source, tvar, termType.TypeConstraint)
                    { tvar with InferredNullable = NullableKnown termType.ForceNullable }
            match invoc with
            | ArgumentWildcard ->
                match aggregate with
                | Some aggregate when aggregate.AllowWildcard -> ArgumentWildcard, term func.Returns
                | _ -> failAt source <| Error.functionDoesNotPermitWildcard func.FunctionName
            | ArgumentList (distinct, args) as argumentList ->
                if Option.isSome distinct then
                    match aggregate with
                    | Some aggregate when aggregate.AllowDistinct -> ()
                    | _ -> failAt source <| Error.functionDoesNotPermitDistinct func.FunctionName
                let nulls = ResizeArray()
                func.ValidateArgs(source, args, (fun a -> a.Source), fun arg termTy ->
                    let term = term termTy
                    ignore <| typeInference.Unify(arg.Source, term, arg.Info.Type)
                    if termTy.ForceNullable then
                        typeInference.ForceNullable(arg.Source, arg.Info.Type.InferredNullable)
                    if termTy.InfectNullable then
                        nulls.Add(arg.Info.Type.InferredNullable))
                let returnType = term func.Returns
                let returnType =
                    if func.Returns.ForceNullable then returnType else
                        { returnType with InferredNullable = InferredNullable.Any(nulls) }
                argumentList, returnType

    let inline implicitAlias column =
        match column with
        | _, (Some _ as a) -> a
        | ColumnNameExpr c, None -> Some c.ColumnName
        | _ -> None

