﻿module Rezoom.SQL.Test.TestTSQL
open System
open NUnit.Framework
open FsUnit
open Rezoom.SQL
open Rezoom.SQL.Mapping

let translate (sql : string) (expectedTSQL : string) =
    let userModel =
        let userModel = userModel1()
        let backend = TSQL.TSQLBackend() :> IBackend
        { userModel with
            Backend = backend
            Model = { userModel.Model with Builtin = backend.InitialModel.Builtin }
        }
    let parsed = CommandEffect.OfSQL(userModel.Model, "anonymous", sql)
    let indexer = { new IParameterIndexer with member __.ParameterIndex(_) = 0 }
    let fragments = userModel.Backend.ToCommandFragments(indexer, parsed.Statements)
    let str = CommandFragment.Stringize(fragments)
    Console.WriteLine(str)
    Assert.AreEqual(expectedTSQL, str)

[<Test>]
let ``at at proc translation`` () =
    translate
        """select datefirst() as d"""
        """SELECT @@DATEFIRST AS [d];"""

[<Test>]
let ``datepart translation`` () =
    translate
        """select dateadd('day', 1, sysutcdatetime()) d"""
        """SELECT dateadd(day,1,sysutcdatetime()) AS [d];"""

[<Test>]
let ``bool to first class`` ()=
    translate
        """select 1 < 0 as b"""
        """SELECT CAST((CASE WHEN (1 < 0) THEN 1 ELSE 0 END) AS BIT) AS [b];"""

[<Test>]
let ``first class to bool`` ()=
    translate
        """select 1 as col from Users where true"""
        """SELECT 1 AS [col] FROM [Users] WHERE ((1)<>0);"""

[<Test>]
let ``iif with predicate`` ()=
    translate
        """select IIF(1 > 0, 'a', 'b') as choice"""
        """SELECT IIF((1 > 0),'a','b') AS [choice];"""

[<Test>]
let ``iif with first class value`` () =
    translate
        """select IIF(false, 'a', 'b') as choice"""
        """SELECT IIF(((0)<>0),'a','b') AS [choice];"""
