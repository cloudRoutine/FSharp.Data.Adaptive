﻿namespace FSharp.Control.Incremental

open System
open System.Threading
open System.Collections.Generic

/// <summary>
/// Transaction holds a set of adaptive objects which
/// have been changed and shall therefore be marked as outOfDate.
/// Commit "propagates" these changes into the dependency-graph, takes
/// care of the correct execution-order and acquires appropriate locks
/// for all objects affected.
/// </summary>
type Transaction() =
    // each thread may have its own running transaction
    [<ThreadStatic; DefaultValue>]
    static val mutable private RunningTransaction : Option<Transaction>

    [<ThreadStatic; DefaultValue>]
    static val mutable private CurrentTransaction : Option<Transaction>

    // we use a duplicate-queue here since we expect levels to be very similar 
    let q = DuplicatePriorityQueue<IAdaptiveObject, int>(fun o -> o.Level)

    // the contained set is useful for determinig if an element has
    // already been enqueued
    let contained = HashSet<IAdaptiveObject>()
    let mutable current : IAdaptiveObject = null
    let mutable currentLevel = 0
    let mutable finalizers : list<unit -> unit> = []

    let runFinalizers () =
        let fs = Interlocked.Exchange(&finalizers, [])
        for f in fs do f()
        
    member x.AddFinalizer (f : unit->unit) =
        Interlocked.Change(&finalizers, (fun a -> f::a) ) |> ignore

    member x.IsContained e = contained.Contains e

    static member Running
        with get() = Transaction.RunningTransaction
        and set r = Transaction.RunningTransaction <- r

    static member Current
        with get() = Transaction.CurrentTransaction
        and set r = Transaction.CurrentTransaction <- r

    static member HasRunning =
        Transaction.RunningTransaction.IsSome
       
    static member RunningLevel =
        match Transaction.RunningTransaction with
            | Some t -> t.CurrentLevel
            | _ -> Int32.MaxValue - 1


    member x.CurrentLevel = currentLevel

    /// <summary>
    /// enqueues an adaptive object for marking
    /// </summary>
    member x.Enqueue(e : IAdaptiveObject) =
        if contained.Add e then
            q.Enqueue e


    member x.CurrentAdapiveObject = 
        if isNull current then None
        else Some current
        

    /// <summary>
    /// performs the entire marking process causing
    /// all affected objects to be made consistent with
    /// the enqueued changes.
    /// </summary>
    member x.Commit() =

        // cache the currently running transaction (if any)
        // and make ourselves current.
        let old = Transaction.RunningTransaction
        Transaction.RunningTransaction <- Some x
        let mutable level = 0
        let myCauses = ref null
        
        let mutable markCount = 0
        let mutable traverseCount = 0
        let mutable levelChangeCount = 0
        let mutable outputs = Array.zeroCreate 8
        while q.Count > 0 do
            // dequeue the next element (having the minimal level)
            let e = q.Dequeue(&currentLevel)
            current <- e

            traverseCount <- traverseCount + 1

            // since we're about to access the outOfDate flag
            // for this object we must acquire a lock here.
            // Note that the transaction will at most hold one
            // lock at a time.
            if e.IsOutdatedCaller() then
                e.AllInputsProcessed(x)

            else
                e.EnterWrite()
                try
                    // if the element is already outOfDate we
                    // do not traverse the graph further.
                    if e.OutOfDate then
                        e.AllInputsProcessed(x)
                    else
                        // if the object's level has changed since it
                        // was added to the queue we re-enqueue it with the new level
                        // Note that this may of course cause runtime overhead and
                        // might even change the asymptotic runtime behaviour of the entire
                        // system in the worst case but we opted for this approach since
                        // it is relatively simple to implement.
                        if currentLevel <> e.Level then
                            q.Enqueue e
                        else
                            // however if the level is consistent we may proceed
                            // by marking the object as outOfDate
                            e.OutOfDate <- true
                            e.AllInputsProcessed(x)
                            markCount <- markCount + 1
                
                            try 
                                // here mark and the callbacks are allowed to evaluate
                                // the adaptive object but must expect any call to AddOutput to 
                                // raise a LevelChangedException whenever a level has been changed
                                if e.Mark() then
                                    // if everything succeeded we return all current outputs
                                    // which will cause them to be enqueued 
                                    outputs <- e.Outputs.Consume()

                                else
                                    // if Mark told us not to continue we're done here
                                    ()

                            with LevelChangedException(obj, objLevel, distance) ->
                                // if the level was changed either by a callback
                                // or Mark we re-enqueue the object with the new level and
                                // mark it upToDate again (since it would otherwise not be processed again)
                                e.Level <- max e.Level (objLevel + distance)
                                e.OutOfDate <- false

                                levelChangeCount <- levelChangeCount + 1

                                q.Enqueue e
                
                finally 
                    e.ExitWrite()

                // finally we enqueue all returned outputs
                for i in 0 .. outputs.Length - 1 do
                    let o = outputs.[i]
                    o.InputChanged(x,e)
                    x.Enqueue o

            contained.Remove e |> ignore
            current <- null
            


        // when the commit is over we restore the old
        // running transaction (if any)
        Transaction.RunningTransaction <- old
        currentLevel <- 0


    member x.Dispose() = 
        runFinalizers()

    interface IDisposable with
        member x.Dispose() = x.Dispose()