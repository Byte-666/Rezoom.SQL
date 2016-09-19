﻿module private SQLow.InferredTypes
open SQLow
open System
open System.Collections.Generic

type TypeVariableId = int

/// Matches SQLite rules for determining the type affinity of a free-form type name.
let private typeAffinityRules =
    [|
        "INT", IntegerType
        "CHAR", StringType
        "CLOB", StringType
        "TEXT", StringType
        "BLOB", BlobType
        "REAL", FloatType
        "FLOA", FloatType
        "DOUB", FloatType
    |]

type InferredType =
    | ConcreteType of ColumnType
    | OneOfTypes of ColumnType list
    /// A type whose nullability depends on that of another type.
    | DependentlyNullType of ifNull: InferredType * thenNull: InferredType
    | TypeVariable of TypeVariableId
    static member Float = ConcreteType { Nullable = false; Type = FloatType }
    static member Integer = ConcreteType { Nullable = false; Type = IntegerType }
    static member Number = OneOfTypes [{ Nullable = false; Type = IntegerType }; { Nullable = false; Type = FloatType }]
    static member String = ConcreteType { Nullable = false; Type = StringType }
    static member Boolean = ConcreteType { Nullable = false; Type = BooleanType }
    static member Blob = ConcreteType { Nullable = false; Type = BlobType }
    static member Any = ConcreteType { Nullable = false; Type = AnyType }
    static member Dependent(ifNull : InferredType, outputType : CoreColumnType) =
        DependentlyNullType(ifNull, ConcreteType { Nullable = false; Type = outputType })
    static member OfLiteral(literal : Literal) =
        match literal with
        | NullLiteral -> ConcreteType { Nullable = true; Type = AnyType }
        | CurrentTimeLiteral
        | CurrentDateLiteral
        | CurrentTimestampLiteral
        | StringLiteral _ -> InferredType.String
        | BlobLiteral _ -> InferredType.Blob
        | NumericLiteral (IntegerLiteral _) -> InferredType.Number
        | NumericLiteral (FloatLiteral _) -> InferredType.Float
    static member Affinity(typeName : TypeName) =
        let names = typeName.TypeName |> Seq.map string |> String.concat " "
        let byRules =
            seq {
                for substr, affinity in typeAffinityRules do
                    if names.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0 then yield affinity
            } |> Seq.tryHead
        match byRules with
        | Some affinity -> affinity
        | None -> FloatType // "numeric" affinity
    static member OfTypeName(typeName : TypeName, inputType : InferredType) =
        let affinity = InferredType.Affinity(typeName)
        match inputType with
        | TypeVariable _ as tv
        | DependentlyNullType (_ as tv, _) -> InferredType.Dependent(tv, affinity)
        | OneOfTypes tys ->
            ConcreteType { Type = affinity; Nullable = tys |> List.exists (fun t -> t.Nullable) }
        | ConcreteType ty ->
            ConcreteType { ty with Type = affinity }

type ITypeInferenceContext =
    abstract member AnonymousVariable : unit -> InferredType
    abstract member Variable : BindParameter -> InferredType
    /// Unify the two types (ensure they are compatible and add constraints)
    /// and produce the most specific type.
    abstract member Unify : InferredType * InferredType -> Result<InferredType, string>
    abstract member Concrete : InferredType -> ColumnType
    abstract member Parameters : BindParameter seq

type InferredQueryColumn =
    {
        FromAlias : Name option
        ColumnName : Name
        InferredType : InferredType
        PrimaryKey : bool // whether this column is part of the primary key of any table
    }
    static member OfTableColumn(column : SchemaColumn) =
        {
            FromAlias = Some column.TableName
            ColumnName = column.ColumnName
            InferredType = ConcreteType column.ColumnType
            PrimaryKey = column.PrimaryKey
        }
    static member OfQueryColumn(alias, column : SchemaQueryColumn) =
        {
            FromAlias = alias
            ColumnName = column.ColumnName
            InferredType = ConcreteType column.ColumnType
            PrimaryKey = column.PrimaryKey
        }

let resultAt source result =
    match result with
    | Ok x -> x
    | Error err -> failAt source err

type NameResolution<'a> =
    | Found of 'a
    | NotFound of string
    | Ambiguous of string

let foundAt source nameResolution =
    match nameResolution with
    | Found x -> x
    | NotFound err
    | Ambiguous err -> failAt source err

type InferredQuery =
    {
        Columns : InferredQueryColumn IReadOnlyList
    }
    static member OfTable(table : SchemaTable) =
        {
            Columns = table.Columns |> Seq.map InferredQueryColumn.OfTableColumn |> toReadOnlyList
        }
    static member OfView(view : SchemaView) =
        let viewName = Some view.ViewName
        let column col =
            InferredQueryColumn.OfQueryColumn(viewName, col)
        {
            Columns = view.Query.Columns |> Seq.map column |> toReadOnlyList
        }
    member this.ColumnByName(name) =
        let matches =
            this.Columns
            |> Seq.filter (fun c -> c.ColumnName = name)
            |> Seq.truncate 2
            |> Seq.toList
        match matches with
        | [] -> NotFound <| sprintf "No such column: ``%O``" name
        | [ single ] -> Found single
        | { FromAlias = Some a1 } :: { FromAlias = Some a2 } :: _ when a1 <> a2 ->
            Ambiguous <|
                sprintf "Ambiguous columm: ``%O`` (may refer to %O.%O or %O.%O)"
                    name a1 name a2 name
        | _ -> Ambiguous <| sprintf "Ambigous column: ``%O``" name
    member this.RenameColumns(names : Name IReadOnlyList) =
        if names.Count <> this.Columns.Count then
            Error <| sprintf "%d columns named for a query with %d columns" names.Count this.Columns.Count
        else
            let newColumns =
                (this.Columns, names)
                ||> Seq.map2 (fun col newName -> { col with ColumnName = newName })
                |> toReadOnlyList
            Ok { Columns = newColumns }
    member this.Append(right : InferredQuery) =
        {
            Columns = appendLists this.Columns right.Columns
        }

type InferredFromClause =
    {
        /// The tables named in the "from" clause of the query, if any.
        /// These are keyed on the alias of the table, if any, or the table name.
        FromVariables : IReadOnlyDictionary<Name, InferredQuery>
        /// The implicit set of columns derived from the "from" clause.
        /// These are the columns you get if you "select *".
        Wildcard : InferredQuery
    }
    member this.ResolveTable(tableName : ObjectName) =
        match tableName.SchemaName with
        // We don't currently support referencing columns like "main.users.id". Use table aliases instead!
        | Some schemaName -> Ambiguous <| sprintf "Unsupported schema name in column reference: ``%O``" tableName
        | None ->
            let succ, query = this.FromVariables.TryGetValue(tableName.ObjectName)
            if succ then Found query
            else NotFound <| sprintf "No such table in FROM clause: ``%O``" tableName.ObjectName
    member this.ResolveColumnReference(name : ColumnName) =
        match name.Table with
        | None -> this.Wildcard.ColumnByName(name.ColumnName)
        | Some tableName ->
            match this.ResolveTable(tableName) with
            | Found tbl -> tbl.ColumnByName(name.ColumnName)
            | NotFound err -> NotFound err
            | Ambiguous err -> Ambiguous err

and InferredSelectScope =
    {
        /// If this scope is that of a subquery, the parent query's scope can also be used
        /// to resolve column and CTE names.
        ParentScope : InferredSelectScope option
        /// The model this select is running against.
        /// This includes tables and views that are part of the database, and may be used to resolve
        /// table names in the "from" clause of the query.
        Model : Model
        /// Any CTEs defined by the query.
        /// These may be referenced in the "from" clause of the query.
        CTEVariables : Map<Name, InferredQuery>
        FromClause : InferredFromClause option
        SelectClause : InferredQuery option
    }

    static member Root(model) =
        {
            ParentScope = None
            Model = model
            CTEVariables = Map.empty
            FromClause = None
            SelectClause = None
        }

    member private this.ResolveTableReferenceBySchema(schema : Schema, name : Name, refTable : SchemaTable -> unit) =
        match schema.Tables.TryFind(name) with
        |  Some tbl ->
            refTable tbl
            Found (InferredQuery.OfTable(tbl))
        | None ->
            match schema.Views.TryFind(name) with
            | Some view ->
                for tbl in view.Query.ReferencedTables do refTable tbl
                Found (InferredQuery.OfView(view))
            | None ->
                NotFound <| sprintf "No such table in schema %O: ``%O``" schema.SchemaName name

    /// Resolve a reference to a table which may occur as part of a TableExpr.
    /// This will resolve against the database model and CTEs, but not table aliases defined in the FROM clause.
    member this.ResolveTableReference(name : ObjectName, refTable : SchemaTable -> unit) =
        match name.SchemaName with
        | None ->
            match this.CTEVariables.TryFind(name.ObjectName) with
            | Some cte -> Found cte
            | None ->
                match this.ParentScope with
                | Some parent ->
                    parent.ResolveTableReference(name, refTable)
                | None ->
                    let schema = this.Model.Schemas.[this.Model.DefaultSchema]
                    this.ResolveTableReferenceBySchema(schema, name.ObjectName, refTable)
        | Some schema ->
            let schema = this.Model.Schemas.[schema]
            this.ResolveTableReferenceBySchema(schema, name.ObjectName, refTable)

    /// Resolve a column reference, which may be qualified with a table alias.
    /// This resolves against the tables referenced in the FROM clause, and the columns explicitly named
    /// in the SELECT clause, if any.
    member this.ResolveColumnReference(name : ColumnName) =
        let findFrom() =
            let thisLevel =
                match this.FromClause with
                | None ->
                    NotFound <| sprintf "Cannot reference column name ``%O`` in query without a FROM clause" name
                | Some fromClause ->
                    fromClause.ResolveColumnReference(name)
            match this.ParentScope, thisLevel with
            | Some parent, NotFound _ ->
                parent.ResolveColumnReference(name)
            | _ -> thisLevel
        match name.Table, this.SelectClause with
        | None, Some selected ->
            match selected.ColumnByName(name.ColumnName) with
            | Found _ as ok -> ok
            | Ambiguous _ as ambig -> ambig
            | NotFound _ -> findFrom()
        | _ -> findFrom()

            
