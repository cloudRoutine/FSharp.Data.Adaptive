﻿module Generators

open System
open FsCheck
open FSharp.Data
open FSharp.Reflection
open System.Reflection
open System.Text.RegularExpressions
open FSharp.Data.Adaptive
open FsUnit

[<AutoOpen>]
module Helpers =

    type TypeVisitor<'r> =
        abstract member Accept<'a> : unit -> 'r

    let private def = typedefof<TypeVisitor<_>>

    let private getMethod =
        let cache = System.Collections.Concurrent.ConcurrentDictionary<Type * Type, MethodInfo>()

        fun (t : Type) (tr : Type) ->
            cache.GetOrAdd((t, tr), fun (t, tr) -> 
                let tv = def.MakeGenericType [|tr|]
                tv.GetMethod("Accept").MakeGenericMethod [| t |]
            )

    type Type with
        member x.Visit(v : TypeVisitor<'r>) =
            let mi = getMethod x typeof<'r>
            mi.Invoke(v, [||]) |> unbox<'r>
   
type refval<'a> = Reference.aval<'a>
type realval<'a> = Adaptive.aval<'a>
type refset<'a> = Reference.aset<'a>
type realset<'a> = Adaptive.aset<'a>

type ChangeGen = 
    {
        change : Gen<unit -> string>
        cell    : obj
    }

type VVal<'a> =
    {
        real : realval<'a>
        ref : refval<'a>
        expression : string
        changes : unit -> list<ChangeGen>
    }
    
type VSet<'a> =
    {
        sreal : realset<'a>
        sref : refset<'a>
        sexpression : string
        schanges : unit -> list<ChangeGen>
    }

module Generators =
    let rand = Random()
    
    let randomFunction<'a, 'b>() =
        let cache = 
            Cache<'a, 'b>(fun _ -> 
                let a = rand.Next()
                let b = rand.Next()
                Arb.generate<'b> |> Gen.eval 30 (Random.StdGen(a, b))
            )
        cache.Invoke

    let randomFunction2<'a, 'b, 'c>() =
        let cache = 
            Cache<'a * 'b, 'c>(fun _ -> 
                let a = rand.Next()
                let b = rand.Next()
                Arb.generate<'c> |> Gen.eval 30 (Random.StdGen(a, b))
            )
        fun a b -> cache.Invoke(a,b)

    let indent (str : string) =
        str.Split("\r\n") |> Array.map (fun s -> "  " + s) |> String.concat "\r\n"

    module Val = 
        let create a b s c = 
            {
                real = a
                ref = b
                expression = s
                changes = c
            }

        let init<'a>() =
            gen {
                let! id = Gen.choose(1, 128)
                let! value = Arb.generate<'a>

                let real = Adaptive.AVal.init value
                let ref = Reference.AVal.init value

                let change =    
                    { 
                        cell = real :> IAdaptiveObject
                        change = 
                            gen {
                                let! newValue = Arb.generate<'a>
                                return fun () ->    
                                    real.Value <- newValue
                                    ref.Value <- newValue
                                    sprintf "C%d <- %A" id newValue

                            }
                    }

                return 
                    create 
                        (real :> Adaptive.aval<_>)
                        (ref :> Reference.aval<_>)
                        (sprintf "c%d" id)
                        (fun () -> [change])
            }

        let constant<'a>() =
            gen {
                let! value = Arb.generate<'a>

                let asString = 
                    match value :> obj with
                    | :? string as str -> System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes str)
                    | _ -> sprintf "%A" value
                return 
                    create
                        (Adaptive.AVal.constant value)
                        (Reference.AVal.constant value)
                        (sprintf "v (%s)" asString)
                        (fun () -> [])
            }

        let map<'a, 'b>() =
            gen {
                let! value = Arb.generate<_>
                //let! f = Arb.generate<'a -> 'b> |> Gen.scaleSize (fun _ -> 50)
                let f = randomFunction<'a, 'b>()
                return 
                    create 
                        (Adaptive.AVal.map f value.real)
                        (Reference.AVal.map f value.ref)
                        (sprintf "map (\r\n%s\r\n)" (indent value.expression))
                        value.changes
            }

        let map2<'a, 'b, 'c>() =
            gen {
                let! value0 = Arb.generate<_>
                let! value1 = Arb.generate<_>
                let f = randomFunction2<'a, 'b, 'c>() //let! f = Arb.generate<'a -> 'b -> 'c> |> Gen.scaleSize (fun _ -> 50)
                return 
                    create 
                        (Adaptive.AVal.map2 f value0.real value1.real)
                        (Reference.AVal.map2 f value0.ref value1.ref)
                        (sprintf "map2 (\r\n%s\r\n%s\r\n)" (indent value0.expression) (indent value1.expression))
                        (fun () -> List.append (value0.changes()) (value1.changes()))
            }

        let bind<'a, 'b>() =
            gen {
                let! value = Arb.generate<VVal<'a>>
                //let! mapping = Arb.generate<'a -> Val<'b>> |> Gen.scaleSize (fun _ -> 50)
                let mapping = randomFunction<'a, VVal<'b>>()

                let changes = value.changes

                let mutable latest = None

                let getChanges() =
                    match latest with
                    | Some l -> List.append (l.changes()) (changes())
                    | None -> changes()

                let mapping (input : 'a) =
                    let res = mapping input
                    latest <- Some res
                    res

                return 
                    create 
                        (Adaptive.AVal.bind (fun v -> (mapping v).real) value.real)
                        (Reference.AVal.bind (fun v -> (mapping v).ref) value.ref)
                        (sprintf "bind (\r\n%s\r\n)" (indent value.expression))
                        getChanges
            }

    module Set =
        let create a b s c = 
            {
                sreal = a
                sref = b
                sexpression = s
                schanges = c
            }

        let init<'a>() =
            gen {
                let! id = Gen.choose(1, 128)
                let! value = Arb.generate<HashSet<'a>>

                let real = Adaptive.cset value
                let ref = Reference.cset value

                let change =    
                    { 
                        cell = real :> obj
                        change = 
                            gen {
                                let! newValue = Arb.generate<HashSet<'a>>
                                return fun () ->    
                                    real.Value <- newValue
                                    ref.Value <- newValue
                                    sprintf "C%d <- %A" id newValue

                            }
                    }

                return 
                    create 
                        (real :> Adaptive.aset<_>)
                        (ref :> Reference.aset<_>)
                        (sprintf "c%d" id)
                        (fun () -> [change])
            }

        let constant<'a>() =
            gen {
                let! value = Arb.generate<HashSet<'a>>

                return 
                    create
                        (Adaptive.ASet.ofHashSet value)
                        (Reference.ASet.ofHashSet value)
                        (sprintf "v(%A)" value)
                        (fun () -> [])
            }

        let map<'a, 'b>() =
            gen {
                let! value = Arb.generate<_>
                //let! f = Arb.generate<'a -> 'b> |> Gen.scaleSize (fun _ -> 50)
                let f = randomFunction<'a, 'b>()
                return 
                    create 
                        (Adaptive.ASet.map f value.sreal)
                        (Reference.ASet.map f value.sref)
                        (sprintf "map (\r\n%s\r\n)" (indent value.sexpression))
                        value.schanges
            }

        let collect<'a, 'b>() =
            gen {
                let! value = Arb.generate<_>
                //let! f = Arb.generate<'a -> 'b> |> Gen.scaleSize (fun _ -> 50)
                let mapping = randomFunction<'a, VSet<'b>>()

                let mutable latest = None

                let changes = value.schanges
                let getChanges() =
                    match latest with
                    | Some l -> List.append (l.schanges()) (changes())
                    | None -> changes()

                let mapping (input : 'a) =
                    let res = mapping input
                    latest <- Some res
                    res

                return 
                    create 
                        (Adaptive.ASet.collect (fun a -> (mapping a).sreal) value.sreal)
                        (Reference.ASet.collect (fun a -> (mapping a).sref) value.sref)
                        (sprintf "collect (\r\n%s\r\n)" (indent value.sexpression))
                        getChanges
            }

type AdaptiveGenerators() =

    static let relevantTypes = 
        [
            typeof<int>
            typeof<obj>
            typeof<HashSet<int>>
            typeof<HashSet<obj>>
        ]

    
    

    static member HashSet<'a>() =
        { new Arbitrary<HashSet<'a>>() with
            member x.Generator =
                Arb.generate<list<'a>> |> Gen.map HashSet.ofList
            member x.Shrinker _ =
                Seq.empty
        }
       
    static member HashMap<'a, 'b>() =
        { new Arbitrary<HashMap<'a, 'b>>() with
            member x.Generator =
                Arb.generate<list<'a * 'b>> |> Gen.map HashMap.ofList
            member x.Shrinker _ =
                Seq.empty
        }
 
    static member Val<'a>() = 
        { new Arbitrary<VVal<'a>>() with
            member x.Generator =
                Gen.sized (fun size ->
                    gen {
                        let! kind = 
                            if size = 0 then
                                Gen.frequency [
                                    1, Gen.constant 0
                                    5, Gen.constant 1
                                ]
                            else 
                                Gen.frequency [
                                    1, Gen.constant 0
                                    5, Gen.constant 1
                                    5, Gen.constant 2
                                    5, Gen.constant 3
                                    5, Gen.constant 4
                                    
                                ]
                        match kind with
                        | 0 -> return! Generators.Val.constant<'a>()
                        | 1 -> return! Generators.Val.init<'a>()
                        | 2 ->
                            let! t = Gen.elements relevantTypes
                            return!
                                t.Visit { new TypeVisitor<_> with member __.Accept<'z>() = Generators.Val.map<'z, 'a>() }
                                |> Gen.scaleSize (fun s -> s - 1)
                        | 3 ->
                            let! t1 = Gen.elements relevantTypes
                            let! t2 = Gen.elements relevantTypes
                            return!
                                t1.Visit { new TypeVisitor<_> with 
                                member __.Accept<'t1>() =
                                    t2.Visit { new TypeVisitor<_> with 
                                    member __.Accept<'t2>() =
                                        Generators.Val.map2<'t1, 't2, 'a>()
                                    }
                                }
                                |> Gen.scaleSize (fun s -> s / 2)

                        | 4 ->
                            let! t = Gen.elements relevantTypes
                            return!
                                t.Visit { new TypeVisitor<_> with
                                member __.Accept<'z>() =
                                    Generators.Val.bind<'z, 'a>()
                                }
                                |> Gen.scaleSize (fun s -> s - 1)

                        | _ ->
                            return failwith ""
                    }
                )
            member __.Shrinker _ = 
                Seq.empty
        }

    static member Set<'a>() = 
        { new Arbitrary<VSet<'a>>() with
            member x.Generator =
                Gen.sized (fun size ->
                    gen {
                        let! kind = 
                            if size = 0 then
                                Gen.frequency [
                                    1, Gen.constant 0
                                    5, Gen.constant 1
                                ]
                            else 
                                Gen.frequency [
                                    1, Gen.constant 0
                                    5, Gen.constant 1
                                    5, Gen.constant 2
                                    2, Gen.constant 3
                                    
                                ]
                        match kind with
                        | 0 -> return! Generators.Set.constant<'a>()
                        | 1 -> return! Generators.Set.init<'a>()
                        | 2 -> 
                            let! t = Gen.elements relevantTypes
                            return!
                                t.Visit { new TypeVisitor<_> with member __.Accept<'z>() = Generators.Set.map<'z, 'a>() }
                                |> Gen.scaleSize (fun s -> s - 1)
                        | _ -> 
                            let! t = Gen.elements relevantTypes
                            return!
                                t.Visit { new TypeVisitor<_> with member __.Accept<'z>() = Generators.Set.collect<'z, 'a>() }
                                |> Gen.scaleSize (fun s -> s / 4)

                    }
                )
            member x.Shrinker _ =
                Seq.empty
        }