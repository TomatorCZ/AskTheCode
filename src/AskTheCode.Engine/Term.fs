namespace AskTheCode

type Sort =
    | Bool
    | Int

type Variable = { Sort: Sort; Name: string }

type Term =
    | Var of Variable
    | IntConst of int
    | BoolConst of bool
    | Add of Term * Term
    | Neg of Term
    | Lt of Term * Term
    | Gt of Term * Term
    | Eq of Term * Term
    | And of Term * Term
    | Or of Term * Term
    | Not of Term

[<RequireQualifiedAccess>]
type BinaryOp = Add | Lt | Gt | Eq | And | Or

[<RequireQualifiedAccess>]
type UnaryOp = Neg | Not

type BinaryOp with  
    member this.Symbol =
        match this with
        | Add -> "+"
        | Lt -> "<"
        | Gt -> ">"
        | Eq -> "=="
        | And -> " && "
        | Or -> " || "
    member this.Sort =
        match this with
        | Add -> Int
        | Lt | Gt | Eq | And | Or -> Bool
    member this.Precedence = 
        match this with
        | Add -> 3
        | Lt | Gt | Eq -> 2
        | And | Or -> 1

type UnaryOp with  
    member this.Symbol =
        match this with
        | Neg -> "-"
        | Not -> "!"
    member this.Sort =
        match this with
        | Neg -> Int
        | Not -> Bool

module Term =

    let (|Binary|Unary|Variable|Constant|) expr =
        match expr with
        | Var v -> Variable v
        | IntConst a -> Constant (Int, a :> System.Object)
        | BoolConst a -> Constant (Bool, a :> System.Object)
        | Add (a, b) -> Binary (BinaryOp.Add, a, b)
        | Neg a -> Unary (UnaryOp.Neg, a)
        | Lt (a, b) -> Binary (BinaryOp.Lt, a, b)
        | Gt (a, b) -> Binary (BinaryOp.Gt, a, b)
        | Eq (a, b) -> Binary (BinaryOp.Eq, a, b)
        | And (a, b) -> Binary (BinaryOp.And, a, b)
        | Or (a, b) -> Binary (BinaryOp.Or, a, b)
        | Not a -> Unary (UnaryOp.Not, a)
    
    let isLeaf expr =
        match expr with
        | Var _ | IntConst _ | BoolConst _ -> true
        | _ -> false

    let updateChildren fn term =
        match term with
        | Var _ | IntConst _ | BoolConst _ -> term
        | Add (a, b) -> Utils.lazyUpdateUnion2 Add fn term (a, b)
        | Neg a -> Utils.lazyUpdateUnion Neg fn term a
        | Lt (a, b) -> Utils.lazyUpdateUnion2 Lt fn term (a, b)
        | Gt (a, b) -> Utils.lazyUpdateUnion2 Gt fn term (a, b)
        | Eq (a, b) -> Utils.lazyUpdateUnion2 Eq fn term (a, b)
        | And (a, b) -> Utils.lazyUpdateUnion2 And fn term (a, b)
        | Or (a, b) -> Utils.lazyUpdateUnion2 Or fn term (a, b)
        | Not a -> Utils.lazyUpdateUnion Not fn term a

    let rec print expr =
        let parensPrint innerExpr = sprintf "(%s)" <| print innerExpr
        match expr with
        | Binary (op, left, right) ->
            let printInner innerExpr =
                match innerExpr with
                | Binary (innerOp, _, _) when innerOp.Precedence < op.Precedence -> parensPrint innerExpr
                | _ -> print innerExpr
            sprintf "%s %s %s" (printInner left) op.Symbol (printInner right)
        | Unary (op, operand) ->
            sprintf "%s%s" op.Symbol <| if isLeaf operand then print operand else parensPrint operand
        | Variable v -> v.Name
        | Constant (_, value) -> value.ToString()
