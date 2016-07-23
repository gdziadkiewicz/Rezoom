﻿namespace Rezoom
open System
open System.Reflection
open System.Reflection.Emit

type private DefaultServiceConstructor =
    static member GetConstructor(ty : Type) =
        if not ty.IsConstructedGenericType then null else
        let tyDef = ty.GetGenericTypeDefinition()
        let stepLocal = tyDef = typedefof<StepLocal<_>>
        let execLocal = tyDef = typedefof<ExecutionLocal<_>>
        if not stepLocal && not execLocal then null else
        let tag =
            if stepLocal then ServiceLifetime.StepLocal
            else ServiceLifetime.ExecutionLocal
        let svcTy = typedefof<LivingService<_>>.MakeGenericType(ty)
        let funcTy = typedefof<Func<_>>.MakeGenericType(svcTy)
        let cons = new DynamicMethod("DynamicConstructor", svcTy, Type.EmptyTypes, true)
        let il = cons.GetILGenerator()
        il.Emit(OpCodes.Ldc_I4, int tag)
        il.Emit(OpCodes.Newobj, ty.GetConstructor(Type.EmptyTypes))
        il.Emit(OpCodes.Newobj, svcTy.GetConstructor([|typeof<ServiceLifetime>; ty|]))
        il.Emit(OpCodes.Ret)
        cons.CreateDelegate(funcTy)

type private DefaultServiceConstructor<'a>() =
    static let constr =
        downcast DefaultServiceConstructor.GetConstructor(typeof<'a>)
        : Func<LivingService<'a>>
    static member Constructor = constr
        
type private DefaultServiceFactory(userFactory : ServiceFactory) =
    inherit ServiceFactory()
    override __.CreateService<'svc>(cxt) =
        let cons = DefaultServiceConstructor<'svc>.Constructor
        if isNull cons then userFactory.CreateService<'svc>(cxt)
        else cons.Invoke()
