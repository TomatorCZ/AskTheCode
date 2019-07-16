namespace AskTheCode.SymbolicExecution

open AskTheCode.Smt
open AskTheCode.Cfg
open AskTheCode.Heap

module Exploration =
    open AskTheCode

    // Definition of functions for path condition handling and the simplest implementation based on solver function

    type ConditionModification =
    | Assert of Assertion: Term
    | Replace of Target: Term * Value: Term

    type ConditionFunctions<'condition> = { GetEmpty: unit -> 'condition; Assert: Term -> 'condition -> 'condition; Solve: 'condition -> SolveResult }

    let solverCondFn solver :ConditionFunctions<Term> = { GetEmpty = (fun () -> BoolConst true); Assert = Utils.curry2 And; Solve = solver }

    let assertCondition condFn cond term =
        match term with
        | BoolConst true -> cond
        | _ -> condFn.Assert term cond

    // Definition of functions for heap handling and default value

    type HeapFunctions<'heap> = { GetEmpty: unit -> 'heap; PerformOp: HeapOperation -> 'heap -> 'heap * ConditionModification option; Merge: seq<'heap> -> 'heap * seq<ConditionModification> }

    let unsupportedHeapFn : HeapFunctions<unit> =
        {
            GetEmpty = id;
            PerformOp = (fun _ _ -> failwith "Heap unsupported");
            Merge = (fun (heaps) -> ((), Seq.replicate (Seq.length heaps) (Assert <| BoolConst true)))
        }

    // Variable version handling

    let getVersion versions name =
        Map.tryFind name versions |> Option.defaultValue 0

    let formatVersioned name version =
        sprintf "%s!%d" name version

    let addVersion versions (variable:Variable) =
        let version = getVersion versions variable.Name
        { variable with Name = formatVersioned variable.Name version }

    let rec addVersions versions term =
        match term with
        | Var v ->
            Var <| addVersion versions v
        | _ ->
            Term.updateChildren (addVersions versions) term

    let mergeVersions versions1 versions2 =
        let mergeItem res name version =
            match Map.tryFind name res with
            | Some currentVersion when currentVersion < version ->
                Map.add name version res
            | None ->
                Map.add name version res
            | _ ->
                res
        Map.fold mergeItem versions2 versions1      // The order to ease folding with Map.empty

    let addHeapOpVersions versions heapOp =
        match heapOp with
        | AssignEquals (trg, left, right) ->
            AssignEquals (addVersion versions trg, left, right)
        | AssignNotEquals (trg, left, right) ->
            AssignNotEquals (addVersion versions trg, left, right)
        | ReadVal (trg, ins, field) ->
            ReadVal (addVersion versions trg, ins, field)
        | WriteVal (ins, field, value) ->
            WriteVal (ins, field, addVersions versions value)
        | _ ->
            heapOp

    // Processing operations into path constraints, version changes and heap updates

    let getAssertFromCondUpdate condUpdate =
        match condUpdate with
        | (Assert cond) -> cond
        | _ -> failwith "Not supported"

    let processOperation heapFn op versions heap =
        match op with
        | (Assign assign) ->
            let trgName = assign.Target.Name
            let trgVersion = getVersion versions trgName
            let target = Var { assign.Target with Name = formatVersioned trgName trgVersion }
            let versions = Map.add trgName (trgVersion + 1) versions
            let value = addVersions versions assign.Value
            (Some (Eq (target, value)), versions, heap)
        | (HeapOp heapOp) ->
            let versionedHeapOp = addHeapOpVersions versions heapOp
            let (heap, heapCond) = heapFn.PerformOp versionedHeapOp heap
            let versions =
                match HeapOperation.targetVariable heapOp with
                | Some { Name = varName } ->
                    let curVersion = getVersion versions varName
                    Map.add varName (curVersion + 1) versions
                | None ->
                    versions
            (Option.map getAssertFromCondUpdate heapCond, versions, heap)

    let processNode heapFn node versions heap =
        let folder op (cond, versions, heap) =
            let (opCond, versions, heap) = processOperation heapFn op versions heap
            (Utils.mergeOptions (Utils.curry2 And) cond opCond, versions, heap)
        match node with
        | Basic (_, operations) ->
            List.foldBack folder operations (None, versions, heap)
        | _ ->
            (None, versions, heap)

    // Explore each path separately

    type ExplorerState<'condition, 'heap> = { Path: Path; Condition: 'condition; Versions: Map<string, int>; Heap: 'heap }

    let run condFn heapFn graph targetNode =
        let extend graph state (edge:Edge) =
            match edge with
            | Inner innerEdge ->
                let node = Graph.node graph innerEdge.From
                let path = Step (node, edge, state.Path)
                let cond =
                    addVersions state.Versions innerEdge.Condition
                    |> assertCondition condFn state.Condition
                let (nodeCond, versions, heap) = processNode heapFn node state.Versions state.Heap
                let cond = Option.fold (assertCondition condFn) cond nodeCond
                { state with Path = path; Condition = cond; Versions = versions; Heap = heap }
            | Outer _ ->
                failwith "Not implemented"

        let rec step states results =
            match states with
            | [] ->
                results
            | state :: states' ->
                let node = Path.node state.Path
                match condFn.Solve state.Condition with
                | Sat _ ->
                    match node with
                    | Enter _ ->
                        step states' (state.Path :: results)
                    | _ ->
                        let states'' =
                            Graph.edgesTo graph node
                            |> List.map (Inner >> extend graph state)
                            |> (fun addedStates -> List.append addedStates states')
                        step states'' results
                | _ ->
                    step states' results
        let states = [ { Path = Target targetNode; Condition = condFn.GetEmpty(); Versions = Map.empty; Heap = heapFn.GetEmpty() } ]
        step states []
        
    // Systematically merge program paths

    let getRelevantExtender graph targetNodeId =
        let nodeCount = List.length graph.Nodes
        let relevant = Array.create nodeCount false

        let markRelevant () (id:NodeId) =
            relevant.[id.Value] <- true
            ((), true)
        Graph.dfs (Graph.backwardExtender graph) markRelevant () graph targetNodeId
        Graph.forwardExtender graph >> List.filter (fun id -> relevant.[id.Value])

    type MergedState<'condition, 'heap> =
        {
            DependencyClosure: Set<NodeId>; Assertion: Term;
            Condition: 'condition;
            VariableVersions: Map<string, int>;
            VariableSorts: Map<string, Sort>;
            Heap: 'heap
        }

    module MergedState =
        let empty (condFn:ConditionFunctions<'cond>) (heapFn:HeapFunctions<'heap>) =
            {
                DependencyClosure = Set.empty;
                Assertion = BoolConst true;
                Condition = condFn.GetEmpty();
                VariableVersions = Map.empty;
                VariableSorts = Map.empty;
                Heap = heapFn.GetEmpty();
            }

        let DependencyClosure state = state.DependencyClosure
        let Assertion state = state.Assertion
        let Condition state = state.Condition
        let VariableVersions state = state.VariableVersions
        let VariableSorts state = state.VariableSorts
        let Heap state = state.Heap

    let mergeRun (condFn:ConditionFunctions<'cond>) (heapFn:HeapFunctions<'heap>) doSolve graph (targetNode:Node) =
        let getNodeCondVar id =
            let name = sprintf "node!!%d" <| NodeId.Value id
            Var { Name = name; Sort = Bool }

        let enterNode = Graph.enterNode graph
        let nodeCount = List.length graph.Nodes

        let relevantExtender = getRelevantExtender graph targetNode.Id

        let states = Array.create<MergedState<'cond, 'heap> option> nodeCount None
        states.[targetNode.Id.Value] <- Some <| MergedState.empty condFn heapFn

        let idToState = NodeId.Value >> Array.get states >> Option.get
        let idToStateProperty getter = idToState >> getter

        let rec processCondition id =

            let addTermVariable varSorts term =
                match term with
                | Var v -> Map.add v.Name v.Sort varSorts
                | _ -> varSorts
            let getTermVariables term =
                Term.fold addTermVariable Map.empty term

            let depIds = relevantExtender id
            for depId in depIds do
                match states.[depId.Value] with
                | None -> processCondition depId
                | Some _ -> ()

            let depStates = List.map idToState depIds

            let edges =
                Graph.edgesFromId graph id
                |> List.filter (fun edge -> List.contains edge.To depIds)

            let depClosure =
                depStates
                |> List.map MergedState.DependencyClosure
                |> Utils.cons (Set.ofList depIds)
                |> Set.unionMany
                |> Set.add id
            
            let (currentAssert, finalVersions, finalHeap) =
                let mergedVersions =
                    depStates
                    |> List.map MergedState.VariableVersions
                    |> List.fold mergeVersions Map.empty
                let (mergedHeap, heapMergeConds) =
                    match depStates with
                    | [ depState ] ->
                        (depState.Heap, Seq.singleton <| BoolConst true)
                    | _ ->
                        depStates
                        |> List.map MergedState.Heap
                        |> Seq.ofList
                        |> heapFn.Merge
                        |> (fun (heap, condOp) -> (heap, Seq.map getAssertFromCondUpdate condOp))
                let getJoinCond (edge:InnerEdge) heapMergeCond =
                    let nextVersions = idToStateProperty MergedState.VariableVersions edge.To
                    let nextVariables =
                        idToStateProperty MergedState.VariableSorts edge.To
                        |> Utils.mergeMaps <| getTermVariables edge.Condition
                    let edgeCond = addVersions nextVersions edge.Condition
                    let versionMergeCond = 
                        let addVarMerge term name sort =
                            let nextVersion = getVersion nextVersions name
                            match getVersion mergedVersions name with
                            | mergedVersion when mergedVersion > nextVersion ->
                                let oldVar = Var { Name = formatVersioned name nextVersion; Sort = sort }
                                let newVar = Var { Name = formatVersioned name mergedVersion; Sort = sort }
                                Term.foldAnd term <| Eq (oldVar, newVar)
                            | _ ->
                                term
                        Map.fold addVarMerge (BoolConst true) nextVariables
                    Term.foldAnd edgeCond versionMergeCond
                    |> Term.foldAnd <| getNodeCondVar edge.To
                    |> Term.foldAnd heapMergeCond

                let joinDisjunction = Seq.map2 getJoinCond edges heapMergeConds |> Term.disjunction
                let node = Graph.node graph id
                let (operationCondOpt, finalVersions, finalHeap) = processNode heapFn node mergedVersions mergedHeap
                let operationCond = Option.defaultValue (BoolConst true) operationCondOpt
                let currentAssert = Implies (getNodeCondVar id, Term.foldAnd joinDisjunction operationCond)
                (currentAssert, finalVersions, finalHeap)

            let variableSorts =
                let addOperationVariables varSorts op =
                    let operationVars =
                        match op with
                        | Assign assign ->
                            getTermVariables assign.Value
                            |> Map.add assign.Target.Name assign.Target.Sort
                        | HeapOp heapOp ->
                            let trgVar =
                                HeapOperation.targetVariable heapOp
                                |> Option.map (fun variable -> Map.add variable.Name variable.Sort Map.empty)
                            let term =
                                HeapOperation.term heapOp
                                |> Option.map getTermVariables
                            Utils.mergeOptions Utils.mergeMaps trgVar term
                            |> Option.defaultValue Map.empty
                    Utils.mergeMaps varSorts operationVars

                let edgeVariables =
                    edges
                    |> List.map (InnerEdge.Condition >> getTermVariables)
                    |> List.fold Utils.mergeMaps Map.empty
                let edgeAndNodeVariables =
                    Node.operations <| Graph.node graph id
                    |> List.fold addOperationVariables edgeVariables
                depStates
                |> List.map MergedState.VariableSorts
                |> List.fold Utils.mergeMaps edgeAndNodeVariables

            let pathCond =
                match depIds with
                | [ onlyId ] ->
                    assertCondition condFn (idToStateProperty MergedState.Condition onlyId) currentAssert
                | (firstId :: otherIds) ->
                    let currentNodes = Set.add firstId <| idToStateProperty MergedState.DependencyClosure firstId
                    let addedAsserts =
                        otherIds
                        |> List.map (idToStateProperty MergedState.DependencyClosure)
                        |> Utils.cons (Set.ofList otherIds)
                        |> Set.unionMany
                        |> Utils.swap Set.difference currentNodes
                        |> List.ofSeq
                        |> List.map (idToStateProperty MergedState.Assertion)
                        |> Utils.cons currentAssert
                    let baseCond = idToStateProperty MergedState.Condition firstId
                    List.fold (assertCondition condFn) baseCond addedAsserts
                | [] ->
                    // No dependencies are only from the target node, which is marked as completed by default
                    failwith "Unreachable"

            let state =
                {
                    DependencyClosure = depClosure;
                    Assertion = currentAssert;
                    Condition = pathCond;
                    VariableVersions = finalVersions;
                    VariableSorts = variableSorts;
                    Heap = finalHeap;
                }

            states.[id.Value] <-
                if id <> enterNode.Id && doSolve id && SolveResult.isUnsat (condFn.Solve pathCond) then    // enterNode WP is solved below
                    let falseAssert = Implies (getNodeCondVar id, BoolConst false)
                    Some { state with Assertion = falseAssert; Condition = condFn.Assert falseAssert pathCond }
                else
                    Some state

        processCondition enterNode.Id
        let cond = condFn.Assert (getNodeCondVar enterNode.Id) states.[enterNode.Id.Value].Value.Condition

        // TODO: Remove once completed
        let termTexts = Array.map (Option.defaultValue (MergedState.empty condFn heapFn) >> fun (s:MergedState<'cond, 'heap>) -> Term.print s.Assertion) states

        // Produce paths according to the model
        let rec gatherResults res cond =
            let rec gatherPath model path =
                let node = Path.node path
                match node with
                | Enter _ ->
                    path
                | _ ->
                    let extendEdge =
                        node
                        |> Node.Id
                        |> Graph.edgesToId graph
                        |> List.find (InnerEdge.From >> getNodeCondVar >> model >> (=) (BoolVal true))
                    let path = Step (Graph.node graph extendEdge.From, Inner extendEdge, path)
                    gatherPath model path

            match condFn.Solve cond with
            | Unsat | Unknown ->
                res
            | Sat model ->
                let path = gatherPath model (Target targetNode)
                // FIXME: Block the repetition of the same path using edge conditions, not nodes
                let pathBlockingTerm =
                    Path.nodes path
                    |> Seq.map (Node.Id >> getNodeCondVar >> Not)
                    |> Term.disjunction
                let cond = condFn.Assert pathBlockingTerm cond
                gatherResults (path :: res) cond

        // TODO (when all aproaches feature it so that the comparison is fair): gatherResults [] cond
        match condFn.Solve cond with
        | Sat _ -> [ Path.Target targetNode ]   // Dummy found path
        | _ -> []

    // Gather weakest precondition at the enter node by simplifying and merging weakest preconditions along the way

    type WeakestPreconditionFn<'wp> =
        {
            GetEmpty: unit -> 'wp;
            GetFalse: unit -> 'wp;
            Assert: Term -> 'wp -> 'wp;
            Replace: Term -> Term -> 'wp -> 'wp;
            Simplify: 'wp -> 'wp;
            Merge: seq<'wp> -> 'wp;
            Solve: 'wp -> SolveResult;
        }

    type WeakestPreconditonState<'wp, 'heap> =
        {
            WeakestPrecondition: 'wp;
            Heap: 'heap;
        }

    module WeakestPreconditionState =
        let empty (wpFn:WeakestPreconditionFn<'wp>) (heapFn:HeapFunctions<'heap>) =
            {
                WeakestPrecondition = wpFn.GetEmpty();
                Heap = heapFn.GetEmpty();
            }

        let WeakestPrecondition state = state.WeakestPrecondition
        let Heap state = state.Heap

    let wpRun (wpFn:WeakestPreconditionFn<'wp>) (heapFn:HeapFunctions<'heap>) doSolve graph (targetNode:Node) =

        let processOperation op heap =
            match op with
            | Assign assign ->
                (heap, Some <| Replace (Var assign.Target, assign.Value))
            | HeapOp heapOp ->
                heapFn.PerformOp heapOp heap

        let applyCondMod wp condMod =
            match condMod with
            | Assert cond -> wpFn.Assert cond wp
            | Replace (trg, value) -> wpFn.Replace trg value wp

        let processNode node wp heap =
            let folder op (wp, heap) =
                let (heap, condModOpt) = processOperation op heap
                (Option.fold applyCondMod wp condModOpt, heap)
            match node with
            | Basic (_, ops) ->
                List.foldBack folder ops (wp, heap)
            | _ ->
                (wp, heap)

        let enterNode = Graph.enterNode graph
        let nodeCount = List.length graph.Nodes

        let relevantExtender = getRelevantExtender graph targetNode.Id

        let states = Array.create<WeakestPreconditonState<'wp, 'heap> option> nodeCount None
        states.[targetNode.Id.Value] <- Some <| WeakestPreconditionState.empty wpFn heapFn

        let rec processCondition id =
            let depIds = relevantExtender id
            for depId in depIds do
                match states.[depId.Value] with
                | None -> processCondition depId
                | Some _ -> ()

            let node = Graph.node graph id
            let depStates = List.map (NodeId.Value >> Array.get states >> Option.get) depIds
            let edges =
                Graph.edgesFromId graph id
                |> List.filter (fun edge -> List.contains edge.To depIds)

            let (mergedHeap, condMods) = depStates |> Seq.map WeakestPreconditionState.Heap |> heapFn.Merge
            let (mergedWp, mergedHeap) =
                condMods
                |> Seq.map2 (WeakestPreconditionState.WeakestPrecondition >> applyCondMod) depStates
                |> Seq.map2 (fun (edge:InnerEdge) wp -> applyCondMod wp <| Assert edge.Condition) edges
                |> Seq.map wpFn.Simplify
                |> wpFn.Merge
                |> fun wp -> processNode node wp mergedHeap

            states.[id.Value] <-
                if node <> enterNode && doSolve id && SolveResult.isUnsat (wpFn.Solve mergedWp) then    // enterNode WP is solved below
                    Some { WeakestPrecondition = wpFn.GetFalse(); Heap = mergedHeap }
                else
                    Some { WeakestPrecondition = mergedWp; Heap = mergedHeap }
        
        processCondition enterNode.Id
        let res = wpFn.Solve states.[enterNode.Id.Value].Value.WeakestPrecondition

        // TODO: Resolve and return the path(s) to reach the target node
        match res with
        | Sat _ -> [ Path.Target targetNode ]   // Dummy found path
        | _ -> []
