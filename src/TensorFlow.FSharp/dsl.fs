namespace TensorFlow.FSharp.DSL

open System
open System.Collections.Generic
open TensorFlow.FSharp

[<AutoOpen>]
module LiveChecking = 
    let livecheck = 
        try 
            match System.Environment.GetEnvironmentVariable("LIVECHECK") with null | "0" -> false | _ -> true 
        with _ -> false

type internal InferenceVarSoln<'T> =
    | Solved of 'T
    | Unsolved

type internal InferenceVar<'T>() = 
    let mutable solution : InferenceVarSoln<'T> = Unsolved
    
    member __.IsSolved = match solution with Solved _ -> true | Unsolved -> false
    member __.Solve sln = solution <- Solved sln
    member __.Solution = solution

/// Represents an inferred dimension
type Dim =
    internal
    /// One dimension is a multiple of another
    | DimMulInt of Dim * int

    /// One dimension is a divisor of another, striding semantics
    | DimDivInt of Dim * int

    /// The dimension is a variable, possibly solved
    | DimVar of InferenceVar<Dim>

    /// The dimension is known
    | DimKnown of int

    override dim.ToString() = 
        match dim.TryValue() with 
        | Some v -> string v
        | None ->  
        match dim with 
        | DimMulInt (expected, n) -> expected.ToString() + "*" + string n 
        | DimDivInt (expected, n) -> expected.ToString() + "/" + string n 
        | DimKnown n -> string n 
        | DimVar v -> 
            match v.Solution with 
            | Unsolved -> "?" 
            | Solved v -> v.ToString()

    member internal dim.StripSolutions() = 
        match dim with 
        | DimVar v -> 
            match v.Solution with 
            | Unsolved -> dim
            | Solved v -> v.StripSolutions()
        | _ -> dim

    member dim.TryValue() = 
        match dim with 
        | DimMulInt (expected,n) -> match expected.TryValue() with None -> None | Some dimv -> Some (dimv*n) 
        | DimDivInt (expected,n) -> match expected.TryValue() with None -> None | Some dimv -> Some (dimv/n + (if dimv % n > 0 then 1 else 0)) 
        | DimKnown n -> Some n 
        | DimVar v -> 
            match v.Solution with 
            | Unsolved -> None 
            | Solved v -> v.TryValue()

    member internal dim.IsSolved = dim.TryValue().IsSome

    member dim.Value = 
        match dim.TryValue() with 
        | Some v -> v
        | None -> -1

    static member ( * ) (dim: Dim, stride: int) = if stride = 1 then dim else DimMulInt (dim, stride)

    static member ( / ) (dim: Dim, stride: int) = if stride = 1 then dim else DimDivInt (dim, stride)

    static member Known n = DimKnown n

    static member Inferred = DimVar (InferenceVar())

    static member Unify op (actual: Dim) (expected: Dim) = 
        match Dim.UnifyInner op actual expected with
        | Ok () -> ()
        | Error msg -> failwithf "mismatched dimensions: expected '%s' but got '%s' for operator %s (%s)" (expected.ToString())  (actual.ToString()) op msg

    static member UnifyInner op (actual: Dim) (expected: Dim) = 
        match actual.TryValue(), expected.TryValue() with 
        | Some v1, Some v2 -> if v1 <> v2 then Error "unequal values" else Ok()
        | _ -> 
        match actual.StripSolutions(), expected.StripSolutions() with 
        // check for identical variables
        | DimVar v1, DimVar v2 when Object.ReferenceEquals(v1,v2) -> Ok ()
        // solve
        | DimVar v1, _ -> v1.Solve expected; Ok()
        | _, DimVar v2 -> v2.Solve actual; Ok()
        | DimKnown d1, DimKnown d2 -> failwith "unreachable - each dimension had value"
        | DimMulInt (d1, n1), DimKnown d2 -> 
            if d2 % n1 <> 0 then 
                Error "not divisible"
            else
                Dim.UnifyInner op d1 (DimKnown (d2 / n1))
        | DimKnown d1, DimMulInt (d2, n2) -> 
            if d1 % n2 <> 0 then 
                Error "not divisible"
            else
                Dim.UnifyInner op (DimKnown (d1 / n2)) d2
        | DimMulInt (d1, n1), DimMulInt (d2, n2) -> 
            if n1 <> n2 then 
                Error "different multipliers"
            else
                Dim.UnifyInner op d1 d2
        | DimDivInt (d1, n1), DimDivInt (d2, n2) -> 
            if n1 <> n2 then 
                Error "different multipliers"
            else
                Dim.UnifyInner op d1 d2
        | _ -> 
            match actual.TryValue(), expected.TryValue() with 
            | None, _ | _, None -> Error "incomplete dimension"
            | _ -> Ok () // equal, see above

/// Represents an inferred shape
type Shape =
    internal
    /// Represents a shape with possible flexibile variable + possible solution
    | ShapeImpl of Dim[] * InferenceVar<Shape> option

    // TODO: this inference is correct for 
    //   scalar --> tensor 
    //   scalar --> vec
    //   scalar --> metrix
    // expansion, but not correct for partial expansion 
    //   vec ---> tensor
    // because the expansion is happening at the wrong end.
    member shape.Item 
        with get idx = 
            match shape with 
            | ShapeImpl (dims, flexopt) -> 
                if idx < dims.Length then 
                    dims.[idx]
                else
                    match flexopt with 
                    | None -> failwithf "index %d out of bounds" idx
                    | Some v ->
                    match v.Solution with 
                    | Unsolved -> Dim.Inferred
                    | Solved sln -> sln.[idx - dims.Length ]

    member shape.AsRank1() = shape.[0]

    member shape.AsRank2() = shape.[0], shape.[1]

    member shape.AsRank3() = shape.[0], shape.[1], shape.[2]

    member shape.AsRank4() = shape.[0], shape.[1], shape.[2], shape.[3] 

    member internal shape.DimensionsWithFlexVar = 
        match shape with 
        | ShapeImpl (dims, None) -> dims, None
        | ShapeImpl (dims, Some v) -> 
            match v.Solution with 
            | Unsolved -> dims, Some v
            | Solved sln -> let dims2, flex = sln.DimensionsWithFlexVar in Array.append dims dims2, flex

    member internal shape.DimensionsEliminatingFlex = 
        let dims, flexvar = shape.DimensionsWithFlexVar
        match flexvar with 
        | None -> ()
        | Some v -> v.Solve (ShapeImpl ([| |], None))
        dims 

    member shape.Dimensions = shape.DimensionsEliminatingFlex

    member shape.AsTFShape() = TFShape(shape.DimensionsEliminatingFlex |> Array.map (fun dim -> int64 dim.Value))

    member shape.AsTFTensor() = shape.AsTFShape().AsTensor()

    static member NoFlex dims = ShapeImpl(dims, None)

    static member Flex dims = ShapeImpl(dims, Some (InferenceVar()))

    static member internal PossibleFlex (flex: bool) dims = if flex then Shape.Flex dims else Shape.NoFlex dims

    static member Inferred with get() = Shape.Flex [| |]
    
    static member UserSpecified (ints: seq<int>) = 
        ints 
        |> Array.ofSeq 
        |> Array.map (fun i -> if i = -1 then Dim.Inferred else DimKnown i)
        |> Shape.NoFlex 

    static member FromTFShapeArray (shape: int64[], ?flex: bool) = 
        let flex = defaultArg flex false
        let dims = shape |> Array.map (fun i -> if i = -1L then Dim.Inferred else DimKnown (int32 i))
        Shape.PossibleFlex flex dims

    static member FromTFShape (shape: TFShape) = 
        shape.ToLongArray() |> Shape.FromTFShapeArray

    static member D with get() = Shape.NoFlex [| DimKnown 1 |]
    
    static member DV with get() = Shape.NoFlex [| Dim.Inferred |]
    
    static member DM with get() = Shape.NoFlex [| Dim.Inferred; Dim.Inferred |]

    /// At least 'n' dimensions, possible more
    static member internal FlexN n = Shape.Flex [| for i in 1 .. n -> Dim.Inferred |]

    static member internal MinDimensions op (shape: Shape) dim = 
        let dims, flexvar = shape.DimensionsWithFlexVar
        if dim > dims.Length then 
            match flexvar with 
            | None -> 
                failwithf "shape %A must have at least %d dimensions for operator %s" shape dim op
            | Some v -> 
                v.Solve (Shape.FlexN dim)

    static member internal  Unify op (actual: Shape) (expected: Shape) = 

        let rec loop (s1: Shape) (s2: Shape) =

            let dims1, flexvar1 = s1.DimensionsWithFlexVar
            let dims2, flexvar2 = s2.DimensionsWithFlexVar

            // Unify those in common - note relies on Seq.iter2 only iterating up to equal length
            (dims1, dims2) ||> Seq.iter2 (fun dim1 dim2 ->
                match Dim.UnifyInner op dim1 dim2 with 
                | Ok () -> ()
                | Error msg -> failwithf "mismatched shapes: expected %A but got %A for operator %s (expected dimension %s but got %s - %s) " expected actual op (dim2.ToString()) (dim1.ToString()) msg
             )

            let n = min dims1.Length dims2.Length
            if n > 0 then
                // Drop front dimensions - shapes smaller
                loop (ShapeImpl(dims1.[n..], flexvar1)) (ShapeImpl(dims2.[n..], flexvar2))

            elif dims1.Length > 0 then
                assert (dims2.Length = 0)
                match flexvar2 with 
                | Some v2 -> 
                    v2.Solve (Shape.FlexN dims1.Length) 
                    // expected now expanded and will have 'n' in common
                    loop s1 s2 
                | None -> 
                    Error ()

            elif dims2.Length > 0 then
                assert (dims1.Length = 0)
                match flexvar1 with 
                | Some v1 -> 
                    v1.Solve (Shape.FlexN dims2.Length) 
                    // actual now expanded and will have 'n' in common
                    loop s1 s2 
                | None -> 
                    Error ()

            else

                match flexvar1, flexvar2 with 
                | Some v1, Some v2 when Object.ReferenceEquals(v1,v2) -> Ok ()
                | Some v1, _ -> v1.Solve (ShapeImpl([| |], flexvar2)); Ok()
                | _, Some v2 -> v2.Solve (ShapeImpl([| |], flexvar1)); Ok()
                | None, None -> Ok()

        match loop actual expected with 
        | Ok () -> ()
        | Error () -> failwithf "mismatched shapes: expected %A but got %A for operator %s" expected actual op

    static member internal EquivShapes op (actual: Shape) (expected: Shape) = 
        Shape.Unify op actual expected
        actual

    override shape.ToString() = 
        let dims, flexvar = shape.DimensionsWithFlexVar
        if dims.Length = 0 then 
            "scalar" 
            + (if flexvar.IsSome then " (can expand)" else "")
        elif dims.Length = 1 then 
            "vector " + dims.[0].ToString()
            + (if flexvar.IsSome then " (can expand)" else "")
        elif dims.Length = 2 then 
            "matrix " + dims.[0].ToString() + " x " + dims.[1].ToString()
            + (if flexvar.IsSome then " (can expand)" else "")
        else
            sprintf "shape %s" (String.concat " x " [ for i in dims -> i.ToString() ]) 
            + (if flexvar.IsSome then "x.." else "")

[<AutoOpen>]
module ShapeHelpers = 

    let memoize (dict: Dictionary<_,_>) key f = 
        match dict.TryGetValue (key) with 
        | true, res -> res
        | _ -> 
            let res = f ()
            dict.[key] <- res
            res

type internal WithScopeDisposable(name:string) = 
    interface IDisposable with 
        member __.Dispose() = ()
    member __.Name = name        


/// Represents a context for turning differentiable tensors into a TensorFlow graph
type internal TFCtxt = 
    { Graph: TFGraph 
      Nodes: Dictionary<DT, TFOutput> // ensure unique nodes from unique DT values
      MomentNodes: Dictionary<DT, TFOutput * TFOutput> // ensure unique nodes from unique DT values
      AddGradientNodes: Dictionary<DT * DT[] * DT option, TFOutput[]> // ensure unique nodes from unique DT values
      Values: Map<string,DT> }

/// Represents a differentiable tensor value, which later corresponds to a node in a TensorFlow graph
and DT internal (shape: Shape, cost: int, makeNode: (TFCtxt -> TFOutput), asTFTensor: (unit -> TFTensor) option) = 

    member internal dt.MakeNode(ctxt: TFCtxt) = 
        memoize ctxt.Nodes dt (fun () -> makeNode ctxt)

    /// Get the inferred shape of the differentiable tensor 
    member __.Shape = shape

    /// Get the inferred shape of the differentiable tensor 
    member internal __.Cost = cost 

    /// A quick check to see if this is a constant tensor, so we don't have to create a graph to
    /// view or analyze it.
    member internal __.TryAsConstTFTensor() = 
        if livecheck then 
            failwith "can't evaluate tensor during LiveCheck"
        match asTFTensor with 
        | None -> None 
        | Some f -> Some (f())

    static member RunTFTensors(values: DT[], ?weights: seq<string * DT>) : TFTensor[] = 
        if livecheck then 
            failwith "can't evaluate tensor during LiveCheck"
        let sess = new TFSession()
        let graph = sess.Graph
        let ctxt = 
            { Graph = graph
              MomentNodes = Dictionary(HashIdentity.Reference)
              AddGradientNodes = Dictionary(HashIdentity.Structural)
              Nodes = Dictionary(HashIdentity.Reference)
              Values = Map.ofSeq (defaultArg weights Seq.empty)}
        let nodes = values |> Array.map (fun value -> value.MakeNode ctxt)
        sess.Run([||],[||],nodes)

    static member RunTFTensor(value: DT, ?weights: seq<string * DT>) : TFTensor = 
        match value.TryAsConstTFTensor() with 
        | None -> DT.RunTFTensors([| value |], ?weights=weights).[0]
        | Some t -> t

    static member Run(value: DT, ?weights: seq<string * DT>) : obj = 
        if livecheck then 
            // TODO: give a better dummy value back here
            obj()
        else
            DT.RunTFTensor(value, ?weights=weights).GetValue() 

    static member Run(values: DT[], ?weights: seq<string * DT>) : obj[] = 
        if livecheck then 
            // TODO: give a better dummy value back here
            [| for v in values -> obj() |]
        else
            let results = DT.RunTFTensors(values, ?weights=weights)
            [| for res in results -> res.GetValue() |]

    /// A method to transform this object to a formattable object, used by F# interactive
    static member PrintTransform(value: DT) = 
        // cost = 0 implies constant, e.g. result from Eval
        match value.TryAsConstTFTensor() with 
        | Some t -> t.GetValue()
        | None -> 
            if value.Cost < 10 then 
                let v = DT.Run(value)
                v
            else
                box (sprintf "%A" value.Shape + " (unevaluated)")

    /// Display constants as data and delayed nodes as shapes
    override dt.ToString() = 
        if livecheck then 
            dt.Shape.ToString()
        else
            // cost = 0 implies constant, e.g. result from Eval
            match dt.TryAsConstTFTensor() with 
            | Some t -> sprintf "%A" (t.GetValue())
            | None -> sprintf "%A" dt.Shape + " (unevaluated)"

/// Represents a differentiable tensor value
type DT<'T> internal (shape: Shape, cost: int, eval: (TFCtxt -> TFOutput), ?asTFTensor: (unit -> TFTensor)) =

    inherit DT(shape, cost, eval, asTFTensor)

    static member inline internal Unop f (input: DT<'T>) : DT<'T> =  
        let outputShape = input.Shape
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> f ctxt.Graph (input.MakeNode ctxt))

    static member inline internal Binop opName f (input1: DT<'T>) (input2: DT<'T>) : DT<'T> = 
        let outputShape = Shape.EquivShapes opName input1.Shape input2.Shape
        let cost = input1.Cost + input2.Cost + 1
        DT<_> (outputShape, cost, fun ctxt -> f ctxt.Graph (input1.MakeNode ctxt) (input2.MakeNode ctxt))

    static member inline internal ReduceOp keep_dims (axis: int[] option) (input: DT<'T>) f : DT<'T> = 
        let outputShape = 
            match keep_dims, axis with
            | Some true, _ -> input.Shape 
            | _, None -> Shape.D
            | _, Some axis -> 
                // TODO: flex here
                let inputDims = input.Shape.DimensionsEliminatingFlex
                let outputDims = inputDims |> Array.indexed |> Array.filter (fun (idx, _) -> not (Array.contains idx axis)) |> Array.map snd
                if outputDims.Length = 0 then Shape.D else Shape.NoFlex outputDims

        let cost = input.Cost + 1
        DT<_> (outputShape, cost, fun ctxt -> 
            let axis = axis |> Option.map (fun axis -> ctxt.Graph.Const(new TFTensor(axis)))
            f ctxt axis (input.MakeNode ctxt))

    static member AddN (inputs: DT<'T>[]) : DT<'T> = 
        let outputShape = inputs.[0].Shape 
        let cost : int = (inputs |> Array.sumBy (fun (v: DT<'T>) -> v.Cost)) + 1
        for v in inputs do Shape.Unify "AddN" outputShape v.Shape
        DT<'T> (outputShape, cost, fun ctxt -> ctxt.Graph.AddN(inputs |> Array.map (fun v -> v.MakeNode ctxt)))

    static member (+) (input1: DT<'T>, input2: DT<'T>) : DT<'T> = 
        // TODO: should this be AddV2
        DT.Binop "(+)" (fun graph node1 node2 -> graph.Add(node1, node2)) input1 input2

    static member (-) (input1: DT<'T>, input2: DT<'T>) : DT<'T> = 
        DT.Binop "(-)" (fun graph node1 node2 -> graph.Sub(node1, node2)) input1 input2

    /// Pointwise multiplication
    static member ( * ) (input1: DT<'T>, input2: DT<'T>) : DT<'T> = 
        DT.Binop "(*)" (fun graph node1 node2 -> graph.Mul(node1, node2)) input1 input2

    /// Pointwise negation
    static member ( ~- ) (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Neg node) input

    static member ( *! ) (input1: DT<'T>, input2: DT<'T>) : DT<'T> = 
        let n1,m,n2 = Dim.Inferred, Dim.Inferred, Dim.Inferred 
        Shape.Unify "MatMul"  input1.Shape (Shape.NoFlex [| n1; m |])
        Shape.Unify "MatMul" input2.Shape (Shape.NoFlex [| m; n2 |])
        let outputShape = Shape.NoFlex [| n1; n2 |]
        let cost = input1.Cost + input2.Cost + 1
        DT<'T> (outputShape, cost, fun ctxt -> ctxt.Graph.MatMul(input1.MakeNode ctxt, input2.MakeNode ctxt))

    static member (/) (input1: DT<'T>, input2: DT<'T>) : DT<'T> = 
        DT.Binop "(/)" (fun graph node1 node2 -> graph.Div(node1, node2)) input1 input2

    static member Abs (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Abs node) input

    static member Acos (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Acos node) input

    static member Acosh (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Acosh node) input

    static member Asin (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Asin node) input

    static member Cos (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Cos node) input

    static member Cosh (input: DT<'T>) : DT<'T> =  
        DT.Unop (fun graph node -> graph.Cosh node) input

    static member Sin (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Sin node) input

    static member Sinh (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Sinh node) input

    static member Sqrt (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Sqrt node) input

    static member Square (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Square node) input

    static member Exp (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Exp node) input

    static member Relu(input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Relu node) input

    static member Tan (input: DT<'T>) : DT<'T> = 
        DT.Unop (fun graph node -> graph.Tan node) input

    static member Tanh (input: DT<'T>) : DT<'T> =  
        DT.Unop (fun graph node -> graph.Tanh node) input

    static member Sum (v: DT<'T>, ?axis: int[], ?keep_dims: bool) : DT<'T> = 
        DT.ReduceOp keep_dims axis v 
            (fun ctxt axis vnode -> ctxt.Graph.ReduceSum(vnode, ?axis=axis, ?keep_dims=keep_dims))

    static member Mean (v: DT<'T>, ?axis: int[], ?keep_dims: bool) : DT<'T> = 
        DT.ReduceOp keep_dims axis v
            (fun ctxt axis vnode -> ctxt.Graph.ReduceMean(vnode, ?axis=axis, ?keep_dims=keep_dims))

    static member Prod (v: DT<'T>, ?axis: int[], ?keep_dims: bool) : DT<'T> = 
        DT.ReduceOp keep_dims axis v 
            (fun ctxt axis vnode -> ctxt.Graph.ReduceProd(vnode, ?axis=axis, ?keep_dims=keep_dims))

    static member Min (input: DT<'T>, ?keep_dims: bool) : DT<'T> = 
        let outputShape = if keep_dims = Some true then input.Shape else Shape.D
        let cost = input.Cost + 1
        DT<_> (outputShape, cost, fun ctxt -> 
           let vnode = input.MakeNode ctxt
           ctxt.Graph.Min(vnode, ctxt.Graph.ReduceDims(vnode), ?keep_dims=keep_dims))

    static member Max (input: DT<'T>, ?keep_dims: bool) : DT<'T> = 
        let outputShape = if keep_dims = Some true then input.Shape else Shape.D
        let cost = input.Cost + 1
        DT<_> (outputShape, cost, fun ctxt -> 
           let vnode = input.MakeNode ctxt
           ctxt.Graph.Max(vnode, ctxt.Graph.ReduceDims(vnode), ?keep_dims=keep_dims))

    // TODO: take the dimension along which to reverse
    static member Reverse (input: DT<'T>) : DT<'T> = 
        let outputShape = input.Shape
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> ctxt.Graph.ReverseV2(input.MakeNode ctxt, ctxt.Graph.Const (new TFTensor( [| 0 |]))))

    static member DiagPart (input: DT<'T>) : DT<'T> = 
        let dims = input.Shape.DimensionsEliminatingFlex
        let n = dims.Length
        if n % 2 <> 0 then invalidArg "DiagPart: v" "expected a tensor with even rank"
        for i in 0 .. n - 1 do 
            Dim.Unify "DiagPart" dims.[i] dims.[n/2 + i]
        let outputShape = Shape.NoFlex (dims.[0 .. n/2 - 1 ])
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> ctxt.Graph.DiagPart(input.MakeNode ctxt))

    static member Norm (v: DT<'T>, ?axis, ?keep_dims: bool) : DT<'T> = 
        DT.Sqrt(DT.Sum(v * v, ?axis=axis, ?keep_dims= keep_dims))

    static member Trace v = 
        DT.Sum (DT.DiagPart v)

    static member TruncatedNormal (?shape: Shape) : DT<'T> = 
        // TODO: is this the correct shape - broadcast?
        let shape = defaultArg shape Shape.Inferred
        let cost = 1
        DT<'T> (shape, cost, fun ctxt -> ctxt.Graph.TruncatedNormal(ctxt.Graph.Const(shape.AsTFTensor()), TFDataType.FromType typeof<'T>))

    /// Supports notation `input.[n]`. Index an element in a vector tensor.
    member input.Item 
        with get (n: int) : DT<'T> = 
            Shape.Unify "Item (index notaion)" input.Shape Shape.DV
            let outputShape = Shape.D
            let cost = input.Cost
            DT<'T>(outputShape, cost, fun ctxt -> 
               let vnode = input.MakeNode ctxt
               let graph = ctxt.Graph
               graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| n |])),graph.Const(new TFTensor( [| 1 |]))), [| 0L |])) // y = xv1

    /// input.[n1,n2]
    member input.Item 
        with get (n1: int, n2: int) : DT<'T> = 
            Shape.Unify "Item (index notation)" input.Shape Shape.DV
            let outputShape = Shape.D
            DT<'T>(outputShape, cost, fun ctxt -> 
               let vnode = input.MakeNode ctxt
               let graph = ctxt.Graph
               graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| n1; n2 |])),graph.Const(new TFTensor( [| 1; 1 |]))), [| 0L; 1L |])) // y = xv1

    /// input.[n1,n2,n3]
    member input.Item 
        with get (n1: int, n2: int,n3: int) : DT<'T> = 
            Shape.Unify "Item (index notation)" input.Shape Shape.DV
            let outputShape = Shape.D
            DT<'T>(outputShape, cost, fun ctxt -> 
               let vnode = input.MakeNode ctxt
               let graph = ctxt.Graph
               graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| n1; n2; n3 |])),graph.Const(new TFTensor( [| 1; 1; 1 |]))), [| 0L; 1L; 2L |])) // y = xv1

    /// input.[n1,n2,n3,n4]
    member input.Item 
        with get (n1: int, n2: int, n3: int, n4: int) : DT<'T> = 
            Shape.Unify "Item (index notation)" input.Shape Shape.DV
            let outputShape = Shape.D
            DT<'T>(outputShape, cost, fun ctxt -> 
               let vnode = input.MakeNode ctxt
               let graph = ctxt.Graph
               graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| n1; n2; n3; n4 |])),graph.Const(new TFTensor( [| 1; 1; 1; 1 |]))), [| 0L; 1L; 2L; 3L |])) // y = xv1

    /// input.[n1..n2] and input.[n1..] and input.[..n2]
    member input.GetSlice(startIndex: int option, endIndex: int option) =
        let inputShape = Shape.NoFlex [| Dim.Inferred |]
        Shape.Unify "GetSlice" input.Shape inputShape
        let startIndex = defaultArg startIndex 0
        let endIndex = defaultArg endIndex -1
        // TODO: this -1 loses information in the case where the input size is unknown
        let len = if endIndex = -1 then (match inputShape.[0].TryValue() with None ->  -1 | Some n -> n) else endIndex - startIndex + 1
        // TODO: this Dim.Inferred is wrong in the case where the input size is unknown
        let dim = if len = -1 then Dim.Inferred else Dim.Known len
        let cost = input.Cost + 1
        let outputShape = Shape.NoFlex [| dim |]
        DT<'T>(outputShape, cost, fun ctxt -> 
            let vnode = input.MakeNode ctxt
            let graph = ctxt.Graph
            graph.Slice(vnode, graph.Const(new TFTensor( [| startIndex |])),graph.Const(new TFTensor( [| len |])))) // y = xv1

    /// input.[n,*] and input.[n,n1..n2]
    member input.GetSlice(idx1: int, startIndex2: int option, endIndex2: int option) =
        let inputShape = Shape.NoFlex [| Dim.Inferred; Dim.Inferred |]
        Shape.Unify "GetSlice" input.Shape inputShape
        let startIndex2 = defaultArg startIndex2 0
        let endIndex2 = defaultArg endIndex2 -1
        let len2 = if endIndex2 = -1 then (match inputShape.[1].TryValue() with None ->  -1 | Some n -> n)  else endIndex2 - startIndex2 + 1 
        // TODO: these Dim.Inferred are wrong in the case where the input size is unknown
        let dim2 = if len2 = -1 then Dim.Inferred else Dim.Known len2
        let outputShape = Shape.NoFlex [| dim2 |]
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> 
            let vnode = input.MakeNode ctxt
            let graph = ctxt.Graph
            graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| idx1; startIndex2 |])),
                               graph.Const(new TFTensor( [| 1; len2 |]))), [| 0L |])) // y = xv1

    /// input.[n,*,*] and input.[n,n1..n2,m1..m2]
    member input.GetSlice(idx1: int, startIndex2: int option, endIndex2: int option, startIndex3: int option, endIndex3: int option) =
        let inputShape = Shape.NoFlex [| Dim.Inferred; Dim.Inferred; Dim.Inferred |]
        Shape.Unify "GetSlice" input.Shape inputShape
        let startIndex2 = defaultArg startIndex2 0
        let endIndex2 = defaultArg endIndex2 -1
        let startIndex3 = defaultArg startIndex3 0
        let endIndex3 = defaultArg endIndex3 -1
        let len2 = if endIndex2 = -1 then (match inputShape.[1].TryValue() with None ->  -1 | Some n -> n)  else endIndex2 - startIndex2 + 1 
        let len3 = if endIndex3 = -1 then (match inputShape.[2].TryValue() with None ->  -1 | Some n -> n)  else endIndex3 - startIndex3 + 1 
        // TODO: these Dim.Inferred are wrong in the case where the input size is unknown
        let dim2 = if len2 = -1 then Dim.Inferred else Dim.Known len2
        let dim3 = if len3 = -1 then Dim.Inferred else Dim.Known len3
        let outputShape = Shape.NoFlex [| dim2; dim3|]
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> 
            let vnode = input.MakeNode ctxt
            let graph = ctxt.Graph
            graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| idx1; startIndex2; startIndex3 |])),
                               graph.Const(new TFTensor( [| 1; len2; len3 |]))), [| 0L |])) // y = xv1

    /// input.[n,*,*,*] and input.[n,n1..n2,m1..m2,p1..p2]
    member input.GetSlice(idx1: int, startIndex2: int option, endIndex2: int option, startIndex3: int option, endIndex3: int option, startIndex4: int option, endIndex4: int option) =
        let inputShape = Shape.NoFlex [| Dim.Inferred; Dim.Inferred; Dim.Inferred; Dim.Inferred |]
        Shape.Unify "GetSlice" input.Shape inputShape
        let startIndex2 = defaultArg startIndex2 0
        let endIndex2 = defaultArg endIndex2 -1
        let startIndex3 = defaultArg startIndex3 0
        let endIndex3 = defaultArg endIndex3 -1
        let startIndex4 = defaultArg startIndex4 0
        let endIndex4 = defaultArg endIndex4 -1
        let len2 = if endIndex2 = -1 then (match inputShape.[1].TryValue() with None ->  -1 | Some n -> n)  else endIndex2 - startIndex2 + 1 
        let len3 = if endIndex3 = -1 then (match inputShape.[2].TryValue() with None ->  -1 | Some n -> n)  else endIndex3 - startIndex3 + 1 
        let len4 = if endIndex4 = -1 then (match inputShape.[3].TryValue() with None ->  -1 | Some n -> n)  else endIndex4 - startIndex4 + 1 
        // TODO: these Dim.Inferred are wrong in the case where the input size is unknown
        let dim2 = if len2 = -1 then Dim.Inferred else Dim.Known len2
        let dim3 = if len3 = -1 then Dim.Inferred else Dim.Known len3
        let dim4 = if len4 = -1 then Dim.Inferred else Dim.Known len4
        let outputShape = Shape.NoFlex [| dim2; dim3; dim4 |]
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> 
            let vnode = input.MakeNode ctxt
            let graph = ctxt.Graph
            graph.Squeeze(graph.Slice(vnode, graph.Const(new TFTensor( [| idx1; startIndex2; startIndex3; startIndex4 |])),
                               graph.Const(new TFTensor( [| 1; len2; len3; len4 |]))), [| 0L |])) // y = xv1

    // TODO: add the remaining slice operations (currently only slicing on first dimension)

    // TODO: handle expansion along multiple arbitrary dimensions
    static member ExpandDims(input: DT<'T>, ?dim: int) : DT<'T> = 
        let dim = defaultArg dim 0
        let inputShape = input.Shape
        // TODO: flex here?
        let inputDims = inputShape.DimensionsEliminatingFlex

        // Although the docs say "insert a dimension of 1" in practice the consumer expands/broadcasts to
        // arbitrary 'n'
        //
        // TODO check that this broadcasting always happens, perhaps BroadcastTo is needed
        let outputShape = Shape.NoFlex [| yield! inputDims.[0 .. dim-1]; yield Dim.Inferred; yield! inputDims.[dim..] |]
        let cost = input.Cost + 1

        DT<'T>(outputShape, cost, fun ctxt -> ctxt.Graph.ExpandDims(input.MakeNode ctxt, ctxt.Graph.Const(new TFTensor( [| dim |] ))))

    //static member Concat (concat_dim: int, vs: seq<DT<'T>>) : DT<'T> = 
    //    let vs = Seq.toArray vs
    //    if vs.Length = 0 then failwith "Vec: zero elements in vector"
    //    let actual = vs.[0].Shape
    //    let outputShape = Shape [| yield! actual.DimensionsEliminatingFlex; yield Dim (vs.Length) |]
    //    DT<'T>(outputShape, cost, fun ctxt -> ctxt.Graph.Concat(ctxt.Graph.Const(new TFTensor(concat_dim)), v.Apply ctxt))

    static member Stack (vs: seq<DT<'T>>, ?axis: int) : DT<'T> = 
        let vs = Seq.toArray vs
        if vs.Length = 0 then failwith "Stack: zero elements in vector"
        let axis = defaultArg axis 0
        let inputShape = vs.[0].Shape
        for v in vs do Shape.Unify "Stack" inputShape v.Shape
        Shape.MinDimensions "Stack" inputShape axis
        let inputDims = inputShape.DimensionsEliminatingFlex
        let outputShape = 
            Shape.NoFlex
                [| yield! inputDims.[0 .. axis - 1]
                   yield DimKnown vs.Length 
                   yield! inputDims.[axis..] |]
        let cost = (vs |> Array.sumBy (fun v -> v.Cost)) + 1
        DT<'T>(outputShape, cost, fun ctxt -> 
            let values = vs |> Array.map (fun v -> v.MakeNode(ctxt))
            ctxt.Graph.Stack(values, axis=axis))

    static member Reshape (input: DT<'T>, shape) : DT<'T> = 
        let cost = input.Cost + 1
        DT<'T>(shape, cost, 
              (fun ctxt -> 
                let node = input.MakeNode(ctxt)
                ctxt.Graph.Reshape(node, ctxt.Graph.Const(shape.AsTFTensor()))))

    static member BroadcastTo (input: DT<'T>, shape) : DT<'T> = 
        let cost = input.Cost + 1
        DT<'T>(shape, cost, 
              (fun ctxt -> 
                let node = input.MakeNode(ctxt)
                ctxt.Graph.BroadcastTo(node, ctxt.Graph.Const(shape.AsTFTensor()))))

    static member AssertShape (shape: Shape) (input: DT<'T>) : DT<'T> = 
        Shape.Unify "AssertShape" input.Shape shape 
        input

    static member inline internal FromConst (dims, asTFTensor, flex: bool option) : DT<'T> = 
        let flex = defaultArg flex false
        let shape = Shape.PossibleFlex flex dims
        let cost = 0
        DT<'T>(shape, cost, 
              (fun ctxt -> 
                let node = ctxt.Graph.Const(asTFTensor())
                if flex then ctxt.Graph.BroadcastTo(node, ctxt.Graph.Const(shape.AsTFTensor())) else node),
              asTFTensor = asTFTensor)

    static member internal FromTFTensor (tensor: TFTensor) : DT<'T> = 
        let shape = Shape.FromTFShapeArray(tensor.Shape)
        let cost = 0
        DT<'T>(shape, cost, 
              (fun ctxt -> 
                let node = ctxt.Graph.Const(tensor)
                ctxt.Graph.BroadcastTo(node, ctxt.Graph.Const(shape.AsTFTensor()))),
              asTFTensor = (fun () -> tensor))

    static member internal ConstInner (obj: obj, ?flex: bool) : DT<'T> = 
        match obj with 
        | :? single
        | :? double 
        | :? int64
        | :? int32 -> () 
        | _ -> failwithf "invalid scalar type %A" (typeof<'T>)
        let asTFTensor () = 
            match obj with 
            | :? single as d -> new TFTensor(d)
            | :? double as d -> new TFTensor(d)
            | :? int32 as d -> new  TFTensor(d)
            | :? int64 as d -> new  TFTensor(d)
            | _ -> failwith "unreachable"
        DT.FromConst([| |], asTFTensor, flex)
    
    static member Const (value: 'T1, ?flex: bool) : DT<'T1> = 
        DT.ConstInner(box value, ?flex=flex)

    static member ConstArray (value: System.Array, ?flex: bool) : DT<'T> = 
        let dims = [| for i in 1 .. value.Rank -> Dim.Known (value.GetLength(i-1)) |]
        DT.FromConst (dims, (fun () -> new TFTensor(value)), flex)

    static member ConstArray1D (value: 'T[], ?flex: bool) : DT<'T> = 
        let dims = [| Dim.Known value.Length |]
        DT.FromConst (dims, (fun () -> new TFTensor(value)), flex)

    static member ConstArray2D (value: 'T[,], ?flex: bool) : DT<'T> = 
        let dims = [| Dim.Known (value.GetLength(0)); Dim.Known (value.GetLength(1))|]
        DT.FromConst (dims, (fun () -> new TFTensor(value)), flex)

    static member ConstArray3D (value: 'T[,,], ?flex: bool) : DT<'T> = 
        let dims = [| Dim.Known (value.GetLength(0)); Dim.Known (value.GetLength(1)); Dim.Known (value.GetLength(2))|]
        DT.FromConst (dims, (fun () -> new TFTensor(value)), flex)

    static member ConstArray4D (value: 'T[,,,], ?flex: bool) : DT<'T> = 
        let dims = [| Dim.Known (value.GetLength(0)); Dim.Known (value.GetLength(1)); Dim.Known (value.GetLength(2)); Dim.Known (value.GetLength(3))|]
        DT.FromConst (dims, (fun () -> new TFTensor(value)), flex)

   /// Add partial deriviatives of loss function
    static member internal AddGradients (y: (* D *) DT<'T>, (* D, DV, DM, ...  *) xs: DT[], (* D *) ?dy: DT<'T>) =  
        Shape.Unify "AddGradients" y.Shape Shape.D
        let key = ((y :> DT),xs,(match dy with None -> None | Some d -> Some (d :> DT)))
        xs |> Array.mapi (fun i x -> 
            let outputShape = x.Shape
            (outputShape, (fun (ctxt: TFCtxt) -> 
                let dynodes = 
                    memoize ctxt.AddGradientNodes key (fun () -> 
                        let xnodes = xs |> Array.map (fun x -> x.MakeNode ctxt)
                        let ynode = y.MakeNode ctxt
                        let dynodesIn = match dy with None -> None | Some z -> Some [| z.MakeNode ctxt |]
                        let dynodes = ctxt.Graph.AddGradients([| ynode |], xnodes, ?dy=dynodesIn)
                        dynodes)
                dynodes.[i]))
             )

    static member Variable (value: DT<'T>, ?name: string) : DT<'T> = 
        let outputShape = value.Shape
        let cost = 100
        DT<'T>(outputShape, cost, fun ctxt -> 
                     let name2 = defaultArg name ""
                     match ctxt.Values.TryFind name2 with 
                     | None -> 
                         printfn "variable nodes not yet supported, and weight '%s' not found in Values, assuming constant" name2
                         //ctxt.Graph.Variable(value.Apply ctxt,name=name2).Read
                         value.MakeNode ctxt
                     | Some t -> 
                         match t with 
                         | :? DT<'T> as vt -> vt.MakeNode ctxt
                         | _ -> 
                         printfn "incorrect type in values, got '%A' expected '%A', assuming variable node is constant" (t.GetType()) (typeof<DT<'T>>)
                         value.MakeNode ctxt
                         )

    static member Conv2D (input: DT<'T>, filters: DT<'T>, ?stride: int, ?padding: string) : DT<'T> = 
    //input: V[N,H,W,C], filters: V[F1;F2;C;COut]) -> output:V[N,H,W,COut] 
        let stride = defaultArg stride 1
        let padding = defaultArg padding "SAME"
        let filtersShape = filters.Shape
        let N, H, W, C = input.Shape.AsRank4()
        let _F1, _F2, C2, COut = filtersShape.AsRank4()
        Dim.Unify "Conv2D" C C2
        let outputShape = Shape.NoFlex [| N; H/stride; W/stride; COut |]
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> ctxt.Graph.Conv2D(input.MakeNode ctxt, filters.MakeNode ctxt,strides = [|1L;int64 stride;int64 stride;1L|], padding=padding))

    // filter: 4-D with shape [filter_height, filter_width, in_channels, out_channels].
    // out_backprop: 4-D with shape [batch, out_height, out_width, out_channels]. Gradients w.r.t. the output of the convolution.
    // input_sizes: An integer vector representing the shape of input, where input is a 4-D [batch, in_height, in_width, in_channels] tensor.
    // Output: 4-D with shape [batch, in_height, in_width, in_channels]. Gradient w.r.t. the input of the convolution.
    // TODO: this doesn't yet allow for fully variable input shapes
    static member Conv2DBackpropInput(filters: DT<'T>, out_backprop: DT<'T>, ?stride: int, ?padding: string) : DT<'T> = 
        let stride = defaultArg stride 1
        let padding = defaultArg padding "SAME"
        let N, out_height, out_width, out_channels = out_backprop.Shape.AsRank4()
        //printfn "out_backprop.Shape = %A" out_backprop.Shape
        let _filter_height, _filter_width, in_channels, out_channels2 = filters.Shape.AsRank4()
        Dim.Unify "Conv2DBackpropInput" out_channels out_channels2
        let input_shape = Shape.NoFlex [| N; out_height*stride; out_width*stride; in_channels |]
        let cost = out_backprop.Cost + 100
        DT<'T>(input_shape, cost, fun ctxt -> 
           //printfn "input_shape = %A" input_shape
           //printfn "out_backprop.Shape = %A" out_backprop.Shape
           let input_sizes = ctxt.Graph.Const(input_shape.AsTFTensor())
           ctxt.Graph.Conv2DBackpropInput(input_sizes, filters.MakeNode ctxt, out_backprop.MakeNode ctxt, strides = [|1L;int64 stride;int64 stride;1L|], padding=padding))

    /// Clips tensor values to a specified min and max.
    static member ClipByValue (input: DT<'T>, low: DT<'T>, high: DT<'T>) : DT<'T> = 
        let outputShape = Shape.EquivShapes "ClipByValue" (Shape.EquivShapes "ClipByValue" input.Shape low.Shape) high.Shape
        let cost = input.Cost + 1
        DT<'T>(outputShape, cost, fun ctxt -> ctxt.Graph.ClipByValue(input.MakeNode ctxt, low.MakeNode ctxt, high.MakeNode ctxt))

    /// Calculate the mean and variance of <c>input</c>
    static member Moments(input: DT<'T>, ?axes: seq<int>) : DT<'T> * DT<'T> = 
        // Note: keep_dims = true
        let outputShape = input.Shape
        let compute (ctxt: TFCtxt) = 
            memoize ctxt.MomentNodes (upcast input) (fun () -> 
                let axes = match axes with None -> None | Some v -> Some (ctxt.Graph.Const(Shape.UserSpecified(v).AsTFTensor()))
                ctxt.Graph.Moments(input.MakeNode ctxt, ?axes=axes,keep_dims=true))
        let cost = input.Cost + 1

        DT<'T>(outputShape, cost, fun ctxt -> fst (compute ctxt)),
        DT<'T>(outputShape, cost, fun ctxt -> snd (compute ctxt))

    /// <summary>
    ///    Decode a JPEG-encoded image to a uint8 tensor.
    /// </summary>
    /// <param name="contents">
    ///    0-D.  The JPEG-encoded image.
    /// </param>
    /// <param name="channels">
    ///    Optional argument. Number of color channels for the decoded image.
    /// </param>
    static member DecodeJpeg(contents:DT<string>, ?channels: int) : DT<int> = // V[int,H,W,C]
        let channels = defaultArg channels 3 // CHECK ME
        let outputShape = Shape.NoFlex [| Dim.Inferred; Dim.Inferred; DimKnown channels |]
        let cost = 1
        DT<_> (outputShape, cost, fun ctxt -> ctxt.Graph.DecodeJpeg(contents=contents.MakeNode ctxt, channels=3L))

    static member Cast<'T2>(input: DT<'T>) : DT<'T2> = 
        let outputShape = input.Shape
        let cost = input.Cost + 1
        DT<_> (outputShape, cost, fun ctxt -> ctxt.Graph.Cast(input.MakeNode ctxt, TFDataType.FromType(typeof<'T2>)))

    static member WithScope(name: string) : IDisposable = 
        new WithScopeDisposable(name) :> _

    static member UsingWithScope (name: string) (f: unit -> DT<'T>) : DT<'T> = 
        let input = f()
        let outputShape = input.Shape
        let cost = input.Cost
        DT<'T>(outputShape, cost, fun ctxt -> use _scope = ctxt.Graph.NameScope(name) in input.MakeNode ctxt)

    static member CreateString(value: byte[]) : DT<string> = 
        DT.FromConst([| |], (fun () -> TFTensor.CreateString(value)), flex = Some true)

    // TODO: improve this
    member value.ToScalar () : 'T = 
        if livecheck then 
            Unchecked.defaultof<'T>
        else
            DT.Run(value) :?> 'T

    /// Execute a computed DT<'T> value, returning a constant DT<'T> value.
    //
    // We use the output shape instead of the input shape since it may contain more information (less flexibility)
    // TODO: add checks that the resulting shape matches the expected shape
    static member Eval (value: DT<'T>, ?weights: seq<string * DT>) : DT<'T> = 
        if livecheck then 
            value
        else
            let tensor = DT.RunTFTensor(value, ?weights=weights)
            DT.FromTFTensor tensor

    /// Execute a pair of DT<'T> values, returning constant DT<'T> values
    static member Eval2 (value1: DT<'T1>, value2: DT<'T2>, ?weights: seq<string * DT>) : DT<'T1> * DT<'T2> = 
        if livecheck then 
            value1, value2
        else
            let values = [| (value1 :> DT); (value2 :> DT) |]
            let tensors = DT.RunTFTensors(values, ?weights=weights)
            DT.FromTFTensor tensors.[0], DT.FromTFTensor tensors.[1]

    /// Execute a triple of DT<'T> values, returning triple of DT<'T> values
    static member Eval3 (value1: DT<'T1>, value2: DT<'T2>, value3: DT<'T3>,  ?weights: seq<string * DT>) : DT<'T1> * DT<'T2> * DT<'T3> = 
        if livecheck then 
            value1, value2, value3
        else
            let values = [| (value1 :> DT); (value2 :> DT); (value3 :> DT) |]
            let tensors = DT.RunTFTensors(values, ?weights=weights)
            DT.FromTFTensor tensors.[0], DT.FromTFTensor tensors.[1], DT.FromTFTensor tensors.[2]

    /// Execute a DT<'T> value and get its value as an object
    member value.GetValue() : obj = 
        if livecheck then 
            // TODO: give a better dummy value back here
            obj()
        else
            DT.Run(value) 

    /// Execute a DT<'T> value and get its value as an array of scalars
    member value.ToArray() : 'T[] = 
        if livecheck then 
            let dim1 = value.Shape.AsRank1()
            Array.zeroCreate dim1 .Value
        else
            DT.Run(value) :?> 'T[]

    /// Execute a DT<'T> value and get its value as a 2D array of scalars
    member value.ToArray2D() : 'T[,] = 
        if livecheck then 
            let dim1, dim2 = value.Shape.AsRank2()
            Array2D.zeroCreate dim1.Value dim2.Value
        else
            DT.Run(value) :?> 'T[,]

    /// Execute a DT<'T> value and get its value as a 3D array of scalars
    member value.ToArray3D() : 'T[,,] = 
        if livecheck then 
            let dim1, dim2, dim3 = value.Shape.AsRank3()
            Array3D.zeroCreate dim1.Value dim2.Value dim3.Value
        else  
            DT.Run(value) :?> 'T[,,]

    /// Execute a DT<'T> value and get its value as a 4D array of scalars
    member value.ToArray4D() : 'T[,,,] = 
        if livecheck then 
            let dim1, dim2, dim3, dim4 = value.Shape.AsRank4()
            Array4D.zeroCreate dim1.Value dim2.Value dim3.Value dim4.Value
        else
            DT.Run(value) :?> 'T[,,,]

    /// Get a DT<'T> value representing zeros
    static member Zero : DT<'T> = 
        DT.ConstInner(box (Unchecked.defaultof<'T>), flex=true)

    /// Get a dummy value with the given shape for use in live checking
    static member Dummy(dims: seq<Dim>) : DT<'T> = 
        DT.FromConst(Array.ofSeq dims, (fun () -> failwith "dummy nodes should not be evaluated during live checking"), flex=Some false)

/// Alias for a tensor scalar.
type Scalar<'T> = DT<'T>

/// Alias for a tensor vector.
type Vector<'T> = DT<'T>

/// Alias for a tensor matrix
type Matrix<'T> = DT<'T>

/// Alias for a 3-dimensional tensor 
type Tensor3<'T> = DT<'T>

/// Alias for a 4-dimensional tensor 
type Tensor4<'T> = DT<'T>

/// Alias for a 5-dimensional tensor 
type Tensor5<'T> = DT<'T>

/// Alias for a tensor vector.
type Vec<'T> = Vector<'T>

/// Alias for a 3-dimensional tensor 
type Tensor<'T> = DT<'T>

/// Alias for a tensor matrix
type Mat<'T> = Matrix<'T>

/// Alias for a 3-dimensional tensor 
type Tns3<'T> = Tensor3<'T>

/// Alias for a 4-dimensional tensor 
type Tns4<'T> = Tensor4<'T>

/// Alias for a 5-dimensional tensor 
type Tns5<'T> = Tensor5<'T>

/// Alias for a tensor scalar.
type Scalar = Scalar<double>

/// Alias for a tensor vector.
type Vec = Vec<double>

/// Alias for a tensor matrix
type Mat = Mat<double>

/// Alias for a 3-dimensional tensor 
type Tns3 = Tensor3<double>

/// Alias for a 4-dimensional tensor 
type Tns4 = Tensor4<double>

/// Alias for a 5-dimensional tensor 
type Tns5 = Tensor5<double>

type Tns = Tensor<double>


/// F#-style module of operations for tensor values
module DT =

    /// Differential changes in scalar `y` with respect to differentials of `xs`. 
    let gradients (y: Scalar<'T>) (xs: DT[]) = 
        DT.AddGradients (y, xs) |> Array.map (fun (shape, f) -> 
            let cost = 100 in DT<'T>(shape, cost, f))

    /// Differential change in scalar `y` with respect to differentials of `x`. 
    let gradient (y: Scalar<'T>) (x: DT<'T>) = 
        (gradients y [| x |]).[0]

    /// Original value and first derivative of a tensor-to-scalar function `f`, at point `x`.
    let evalAndDiff (f: DT<'T> -> Scalar<'T>) (x: DT<'T>) = 
        let y = f x
        y, gradient y x

    /// First derivative of a scalar-to-scalar function `f`, at point `x`.
    let diff (f: Scalar<'T> -> Scalar<'T>) x = evalAndDiff f x |> snd

    /// Second derivative of a scalar-to-scalar function `f`, at point `x`.
    let diff2 (f: Scalar<'T> -> Scalar<'T>) x  : Scalar<'T> =
        diff (diff f) x

    /// Original value, first derivative, and second derivative of a scalar-to-scalar function `f`, at point `x`.
    let evalAndDiffAndDiff2 (f: Scalar<'T> -> Scalar<'T>) x : Scalar<'T> * Scalar<'T> * Scalar<'T> =
        let v, d = evalAndDiff f x
        let d2 = diff2 f x
        (v, d, d2)

    /// Original value and second derivative of a scalar-to-scalar function `f`, at point `x`.
    let diffAndDiff2 (f: Scalar<'T> -> Scalar<'T>) x  : Scalar<'T> * Scalar<'T> =
        evalAndDiffAndDiff2 f x |> (fun (a,_,c) -> a,c)

    /// `n`-th derivative of a scalar-to-scalar function `f`, at point `x`.
    let diffN n (f: Scalar<'T> -> Scalar<'T>) x  : Scalar<'T> =
        if n < 0 then invalidArg "n" "must be positive"
        elif n = 0 then f x
        else
            let rec d n f =
                match n with
                | 1 -> diff f
                | _ -> d (n - 1) (diff f)
            x |> d n f

    /// Original value and `n`-th derivative of a scalar-to-scalar function `f`, at point `x`.
    let evalAndDiffN n (f: Scalar<'T> -> Scalar<'T>) x  : Scalar<'T> * Scalar<'T> =
        (x |> f, diffN n f x)

    /// Original value and gradient of a vector-to-scalar function `f`, at point `x`. Reverse AD.
    let evalAndGrad (f: Vec<'T> -> Scalar<'T>) (x: Vec<'T>) : Scalar<'T> * Vec<'T> = 
        Shape.Unify "evalAndGrad" x.Shape Shape.DV
        let y = f x
        let dy = gradient y x
        y, dy

    /// Gradient of a vector-to-scalar function `f`, at point `x`. Reverse AD.
    let grad (f: Vec<'T> -> Scalar<'T>) x : Vec<'T> =
        evalAndGrad f x |> snd

(*
    /// Original value and gradient-vector product (directional derivative) of a vector-to-scalar function `f`, at point `x`, along vector `v`.
    let gradv' (f: DV<'T> -> D<'T>) x (v: DV<'T>) : D<'T> * D<'T> =
        let yv = f v
        let y = f x
        let dyv = DT.AddGradients (y, x, yv)
        y, dyv

    /// Gradient-vector product (directional derivative) of a vector-to-scalar function `f`, at point `x`, along vector `v`.
    let gradv (f: DV<'T> -> D<'T>) x v : D<'T> =
        gradv' f x v |> snd

    /// Original value and Jacobian-vector product of a vector-to-vector function `f`, at point `x`, along vector `v`.
    let jacobianv' (f: DV<'T> -> DV<'T>) (x: DV<'T>) (v: DV<'T>) : DV<'T> * DV<'T> =
        Shape.Unify x.Shape Shape.DV
        Shape.Unify v.Shape Shape.DV
        let yv = f v
        let y = f x
        let ysize = 
            match y.Shape.DimensionsEliminatingFlex.[0].TryValue() with 
            | None -> failwith "unknown vector output size in jacobian"
            | Some d -> d
        let dyv = DT.Stack [| for i in 0 .. ysize-1 -> DT.AddGradients (y.[i], x, yv.[i]) |]
        y, dyv

    /// Jacobian-vector product of a vector-to-vector function `f`, at point `x`, along vector `v`.
    let jacobianv (f: DV<'T> -> DV<'T>) x v : DV<'T> =
        jacobianv' f x v |> snd
*)
    /// Original value and Jacobian of a vector-to-vector function `f`, at point `x`. Forward or reverse AD, depending on input and output dimensions.
    let evalAndJacobian (f: Vec<'T> -> Vec<'T>) (x:Vec<'T>) : Vec<'T> * Mat<'T> =
        let y = f x
        let ysize = 
            match y.Shape.DimensionsEliminatingFlex.[0].TryValue() with 
            | None -> failwith "unknown vector output size in jacobian"
            | Some d -> d
        let jydx = DT.Stack [| for i in 0 .. ysize - 1 -> gradient y.[i] x |]
        y, jydx

    /// Jacobian of a vector-to-vector function `f`, at point `x`. Forward or reverse AD, depending on input and output dimensions.
    let jacobian (f: Vec<'T> -> Vec<'T>) x : Mat<'T> =
        evalAndJacobian f x |> snd

    /// Gradient and Hessian of a vector-to-scalar function `f`, at point `x`. Forward-on-reverse AD.
    let gradAndHessian (f: Vec<'T> -> Scalar<'T>) x : Vec<'T> * Mat<'T> =
        evalAndJacobian (grad f) x

    /// Original value, gradient, and Hessian of a vector-to-scalar function `f`, at point `x`. Forward-on-reverse AD.
    let evalAndGradAndHessian (f: Vec<'T> -> Scalar<'T>) x : Scalar<'T> * Vec<'T> * Mat<'T> =
        let g, h = gradAndHessian f x
        (f x, g, h)

    /// Hessian of a vector-to-scalar function `f`, at point `x`. Forward-on-reverse AD.
    let hessian (f: Vec<'T> -> Scalar<'T>) x : Mat<'T> =
        jacobian (grad f) x

    /// Original value and Hessian of a vector-to-scalar function `f`, at point `x`. Forward-on-reverse AD.
    let evalAndHessian (f: Vec<'T> -> Scalar<'T>) x : Scalar<'T> * Mat<'T> =
        (x |> f, hessian f x)

(*
    /// Original value, gradient-vector product (directional derivative), and Hessian-vector product of a vector-to-scalar function `f`, at point `x`, along vector `v`. Reverse-on-forward AD.
    let gradAndHessianv' (f: DV<'T> -> D<'T>) x v =
        let gv, hv = evalAndGrad (fun xx -> gradv f xx v) x
        (x |> f, gv, hv)

    /// Gradient-vector product (directional derivative) and Hessian-vector product of a vector-to-scalar function `f`, at point `x`, along vector `v`. Reverse-on-forward AD.
    let gradAndHessianv (f: DV<'T> -> D<'T>) x v : D<'T> * DV<'T> =
        gradAndHessianv' f x v |> (fun (_,b,c) -> b,c)

    /// Original value and Hessian-vector product of a vector-to-scalar function `f`, at point `x`, along vector `v`. Reverse-on-forward AD.
    let hessianv' (f: DV<'T> -> D<'T>) x v =
        gradAndHessianv' f x v |> (fun (a,_,c) -> a,c)

    /// Hessian-vector product of a vector-to-scalar function `f`, at point `x`, along vector `v`. Reverse-on-forward AD.
    let hessianv (f: DV<'T> -> D<'T>) x v : DV<'T> =
        hessianv' f x v |> snd
*)

    let trace v = DT.Sum (DT.DiagPart v)

    /// Original value and Laplacian of a vector-to-scalar function `f`, at point `x`. Reverse-on-forward AD.
    let evalAndLaplacian (f: Vec<'T> -> Scalar<'T>) x : Scalar<'T> * Scalar<'T> = 
        let v, h = evalAndHessian f x
        (v, trace h)

    /// Laplacian of a vector-to-scalar function `f`, at point `x`. Reverse-on-forward AD.
    let laplacian (f: Vec<'T> -> Scalar<'T>) x : Scalar<'T> =
        evalAndLaplacian f x |> snd

    /// Original value and curl of a vector-to-vector function `f`, at point `x`. Supported only for functions with a three-by-three Jacobian matrix.
    let evalAndCurl (f: Vec<'T> -> Vec<'T>) x =
        let v, j = evalAndJacobian f x
        //if (j.Rows, j.Cols) <> (3, 3) then ErrorMessages.InvalidArgCurl()
        v, DT.Stack [|j.[1, 2] - j.[2, 1]; j.[2, 0] - j.[0, 2]; j.[0, 1] - j.[1, 0]|]

    /// Curl of a vector-to-vector function `f`, at point `x`. Supported only for functions with a three-by-three Jacobian matrix.
    let curl (f: Vec<'T> -> Vec<'T>) x : Vec<'T> =
        evalAndCurl f x |> snd

    /// Original value and divergence of a vector-to-vector function `f`, at point `x`. Defined only for functions with a square Jacobian matrix.
    let evalAndDivergence (f: Vec<'T> -> Vec<'T>) x =
        let v, j = evalAndJacobian f x
        //if j.Rows <> j.Cols then ErrorMessages.InvalidArgDiv()
        v, DT.Trace j

    /// Divergence of a vector-to-vector function `f`, at point `x`. Defined only for functions with a square Jacobian matrix.
    let divergence (f: Vec<'T> -> Vec<'T>) x : Scalar<'T> =
        evalAndDivergence f x |> snd

    /// Original value, curl, and divergence of a vector-to-vector function `f`, at point `x`. Supported only for functions with a three-by-three Jacobian matrix.
    let evalAndCurlAndDivergence (f: Vec<'T> -> Vec<'T>) x =
        let v, j = evalAndJacobian f x
        //if (j.Rows, j.Cols) <> (3, 3) then ErrorMessages.InvalidArgCurlDiv()
        v, DT.Stack [|j.[1, 2] - j.[2, 1]; j.[2, 0] - j.[0, 2]; j.[0, 1] - j.[1, 0]|], trace j

    /// Curl and divergence of a vector-to-vector function `f`, at point `x`. Supported only for functions with a three-by-three Jacobian matrix.
    let curlAndDivergence (f: Vec<'T> -> Vec<'T>) x : Vec<'T> * Scalar<'T> =
        evalAndCurlAndDivergence f x |> (fun (_,b,c) -> b,c)

    /// Convert the input to an array if possible
    let toArray (input: DT<'T>) = input.ToArray()
    
    /// Convert the input to an array if possible
    let toArray2D (input: DT<'T>) = input.ToArray2D()
    
    /// Convert the input to an array if possible
    let toArray3D (input: DT<'T>) = input.ToArray3D()
    
    /// Convert the input to an array if possible
    let toArray4D (input: DT<'T>) = input.ToArray4D()
    
    /// Convert the input to a scalar if possible
    let toScalar (input: DT<'T>) = input.ToScalar()

type TensorFlow = ReflectedDefinitionAttribute
type Model = ReflectedDefinitionAttribute

/// fm { ... }  is a computational DSL (not yet using ReflectedDefinition) that does a layer of shape inference 
/// in the first runtime phase, and then the actual graph construction in the second runtime phase.   
type FMBuilder() =
    member x.Return(v: DT<'T>) = v
    
    /// Supports the use of `use _ = ...` in tf expressions
    member x.Using(v: IDisposable, f: (unit -> DT<'T>)) = 
        match v with 
        | :? WithScopeDisposable as w -> DT.UsingWithScope w.Name f
        | _ -> use x = v in f()

[<AutoOpen>]
module FMHelpers = 

    /// Create a concrete tensor shape. -1 can be used for unknown (inferred) dimensions
    let fm = FMBuilder()

    /// Create a concrete tensor shape. -1 can be used for unknown (inferred) dimensions
    let shape (ints: int list) = Shape.UserSpecified ints

    /// Create a scalar node (with implicit broadcast)
    let scalar (value:'T) : DT<'T> = DT.Const (value, flex=true)

    /// Create a scalar node (with implicit broadcast)
    let v x = scalar x

    /// Create a vector from raw data
    let vec (data:seq<'T>) : DT<'T> = 
        let d = Seq.toArray data
        DT.ConstArray1D(d, flex=false)

    /// Create a vector from existing differentiable tensors
    let vecOfScalars (xs:seq<DT<'T>>) : DT<'T> = 
        DT.Stack xs

    /// Extend the scalar node, adding a batch dimension
    let batchOfScalars d = vec d

    /// Create a matrix from raw data
    let matrix (data: seq< #seq<'T>>) : DT<'T> = 
        let data = array2D data 
        DT.ConstArray2D(data, flex=false)

    /// Create a matrix by stacking existing vectors of differentiable tensors
    let matrixOfVecs (ds:seq<DT<'T>>) : DT<'T> = 
        DT.Stack ds

    /// Extend the vector node, adding a batch dimension
    let batchOfVecs vecs = matrix vecs 

    /// Create a non-jagged 3D array from jagged data
    let array3D data = 
        let data = data |> Array.ofSeq |> Array.map array2D
        let r1, r2, r3 = data.Length, data.[0].GetLength(0), data.[0].GetLength(1)
        if (r1 <> r2) || r2 <> r3 then invalidArg "data" (sprintf "jagged input: %d x %d x %d" r1 r2 r3)
        Array3D.init r1 r2 r3 (fun i j k -> data.[i].[j,k])

    /// Create a non-jagged 4D array from jagged data
    let array4D data = 
        let data = data |> array2D |> Array2D.map array2D
        let r1,r2,r3,r4 = (data.GetLength(0), data.GetLength(1), data.[0,0].GetLength(0),data.[0,0].GetLength(1))
        if (r1 <> r2) || r2 <> r3 || r3 <> r4 then invalidArg "data" (sprintf "jagged input: %d x %d x %d x %d" r1 r2 r3 r4)
        Array4D.init r1 r2 r3 r4 (fun i j k m -> data.[i,j].[k,m])

    /// Create a rank-3 tensor from raw data
    let tensor3 (data: seq< #seq< #seq<'T>>>) : DT<'T> = 
        DT.ConstArray3D(array3D data, flex=false)

    /// Makes a tensor from a 1D array representing a pixel of an image. The inferred tensor shape
    /// may be larger if the tensor value is used in a construct where broadcasting is required.
    let pixel data = 
        DT.ConstArray1D(data, flex=true)

    /// Makes a tensor from a 3D array representing the pixels of an image as input. The inferred tensor shape
    /// may be larger if the tensor value is used in a construct where broadcasting is required.
    let image data = 
        DT.ConstArray3D(array3D data, flex=true)

    /// Create a rank-4 tensor from raw data
    let tensor4 (data: seq< #seq< #seq< #seq<'T>>>>) : DT<'T> = 
        DT.ConstArray4D(array4D data, flex=false)

    let batchOfImages d = tensor4 d 
    
    /// Makes a tensor from a 4D array representing video frames as input. The inferred tensor shape
    /// may be larger if the tensor value is used in a construct where broadcasting is required.
    let video data = 
        DT.ConstArray4D(array4D data, flex=true)

    //let batchOfVideos d = tensor5 d 
    
    /// The pointwise relu function of the elements of a tensor
    let relu (x: DT<'T>) : DT<'T> = 
        DT.Relu(x)
        // We can't use this because of reflection issues for the live check interpreter
        //(DT<'T>: (static member Relu : DT<'T> -> DT<'T>) (x))

    /// The sum of the elements of a tensor
    let sum (x: DT<'T>) : DT<'T> = 
        DT.Sum(x)
        // We can't use this because of reflection issues for the live check interpreter
        //(DT<'T>: (static member Sum : DT<'T> -> DT<'T>) (x))

    /// The product of the elements of a tensor
    let prod (x: DT<'T>) : DT<'T> = 
        DT.Prod(x)
        //(DT<'T>: (static member Prod : DT<'T> -> DT<'T>) (x))
        // We can't use this because of reflection issues for the live check interpreter

    /// The average value of the elements of a tensor
    let mean (x: DT<'T>) : DT<'T> = 
        DT.Mean(x)
        // We can't use this because of reflection issues for the live check interpreter
        //(DT<'T>: (static member Mean : DT<'T> -> DT<'T>) (x))

    /// The max value of the elements of a tensor
    let maxValue (x: DT<'T>) : DT<'T> = 
        DT.Max(x)
        // We can't use this because of reflection issues for the live check interpreter
        //(DT<'T>: (static member Max : DT<'T> -> DT<'T>) (x))

    /// The min value of the elements of a tensor
    let minValue (x: DT<'T>) : DT<'T> = 
        DT.Min(x)
        // We can't use this because of reflection issues for the live check interpreter
        //(DT<'T>: (static member Min : DT<'T> -> DT<'T>) (x))


    /// The norm of a tensor
    let norm (x: DT<'T>) : DT<'T> = 
        DT.Norm(x)
        // We can't use this because of reflection issues for the live check interpreter
        //(DT<'T>: (static member Norm : DT<'T> -> DT<'T>) (x))

    let inline sqr x = x * x

    /// The global random number generator
    let rnd = new System.Random()

    /// Generate a randome number using the global generator
    let rand() = rnd.NextDouble()

    let crossEntropy (x:DT<_>) (y:DT<_>) : DT<double> = failwith "fail"
    //    -(x |> DM.toCols |> Seq.mapi (fun i v -> 
    //        (DV.standardBasis v.Length (int (float y.[0, i]))) * log v) |> Seq.sum) / x.Cols

    /// Change a 3D-array to friendly notation
    let friendly3D (d : 'T[,,]) =
        [| for i in 0..Array3D.length1 d - 1 -> [| for j in 0..Array3D.length2 d - 1 -> [| for k in 0..Array3D.length3 d - 1 -> d.[i,j,k]  |]|]|]
        |> Array.map array2D

    /// Change an 4D-array to friendly notation
    let friendly4D (d : 'T[,,,]) =
        [| for i in 0..Array4D.length1 d - 1 -> [| for j in 0..Array4D.length2 d - 1 -> [| for k in 0..Array4D.length3 d - 1 -> [| for m in 0..Array4D.length4 d - 1 -> d.[i,j,k,m]  |]|]|]|]
        |> array2D |> Array2D.map array2D

    /// Extend the value in the batch dimension
    let batchExtend (v: DT<'T>) = DT.ExpandDims v

    /// Create a batch of values
    let batch  (vs: seq<DT<'T>>) = DT.Stack vs

    /// Create a variable placeholder node
    let variable value name = DT.Variable (value, name)

[<AttributeUsage(AttributeTargets.Field ||| AttributeTargets.Property ||| AttributeTargets.Method)>]
type LiveCheckAttribute() =
    inherit Attribute()

[<AttributeUsage(AttributeTargets.Field ||| AttributeTargets.Property ||| AttributeTargets.Method)>]
type LiveTestAttribute() =
    inherit Attribute()
