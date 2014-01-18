﻿namespace FSharp.Data.Experimental

open System
open System.Data
open System.Data.SqlClient
open System.Reflection
open System.Collections.Generic
open System.Threading
open System.Diagnostics
open Microsoft.SqlServer.Server

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

open FSharp.Data.Experimental.Internals

open Samples.FSharp.ProvidedTypes

type ResultType =
    | Tuples = 0
    | Records = 1
    | DataTable = 2
    | Maps = 3

[<assembly:TypeProviderAssembly()>]
do()

[<TypeProvider>]
type public SqlCommandProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let mutable watcher = null : IDisposable

    let nameSpace = this.GetType().Namespace
    let assembly = Assembly.GetExecutingAssembly()
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlCommand", Some typeof<obj>, HideObjectMethods = true)

    do 
        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("CommandText", typeof<string>) 
                ProvidedStaticParameter("ConnectionString", typeof<string>, "") 
                ProvidedStaticParameter("ConnectionStringName", typeof<string>, "") 
                ProvidedStaticParameter("CommandType", typeof<CommandType>, CommandType.Text) 
                ProvidedStaticParameter("ResultType", typeof<ResultType>, ResultType.Tuples) 
                ProvidedStaticParameter("SingleRow", typeof<bool>, false) 
                ProvidedStaticParameter("ConfigFile", typeof<string>, "") 
                ProvidedStaticParameter("AllParametersOptional", typeof<bool>, false) 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
            ],             
            instantiationFunction = this.CreateType
        )
        this.AddNamespace(nameSpace, [ providerType ])
    
    interface IDisposable with 
        member this.Dispose() = 
           if watcher <> null
           then try watcher.Dispose() with _ -> ()

    member internal this.CreateType typeName parameters = 
        let commandText : string = unbox parameters.[0] 
        let connectionStringProvided : string = unbox parameters.[1] 
        let connectionStringName : string = unbox parameters.[2] 
        let commandType : CommandType = unbox parameters.[3] 
        let resultType : ResultType = unbox parameters.[4] 
        let singleRow : bool = unbox parameters.[5] 
        let configFile : string = unbox parameters.[6] 
        let allParametersOptional : bool = unbox parameters.[7] 
        let dataDirectory : string = unbox parameters.[8] 

        let resolutionFolder = config.ResolutionFolder
        let commandText, watcher' = 
            Configuration.ParseTextAtDesignTime(commandText, resolutionFolder, this.Invalidate)
        watcher' |> Option.iter (fun x -> watcher <- x)
        let designTimeConnectionString =  Configuration.GetConnectionString(resolutionFolder, connectionStringProvided, connectionStringName, configFile)
        
        use conn = new SqlConnection(designTimeConnectionString)
        conn.Open()
        conn.CheckVersion()
        conn.LoadDataTypesMap()

        let providedCommandType = ProvidedTypeDefinition(assembly, nameSpace, typeName, baseType = Some typeof<obj>, HideObjectMethods = true)

        let sqlParameters = this.ExtractSqlParameters(conn, commandText, commandType)

        let ctor = ProvidedConstructor( [ ProvidedParameter("connectionString", typeof<string>, optionalValue = Unchecked.defaultof<string>) ])
        ctor.InvokeCode <- fun args -> 
            <@@ 
                let runTimeConnectionString = 
                    if String.IsNullOrEmpty(%%args.[0])
                    then

                        Configuration.GetConnectionString (resolutionFolder, connectionStringProvided, connectionStringName, configFile)
                    else 
                        %%args.[0]
                do
                    if dataDirectory <> ""
                    then AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectory)

                let this = new SqlCommand(commandText, new SqlConnection(runTimeConnectionString)) 
                this.CommandType <- commandType
                let xs = %%Expr.NewArray(typeof<SqlParameter>, List.map QuotationsFactory.ToSqlParam sqlParameters)
                this.Parameters.AddRange xs
                this
            @@>

        providedCommandType.AddMember ctor

        let executeArgs = this.GetExecuteArgsForSqlParameters(providedCommandType, sqlParameters, allParametersOptional) 
        let outputColumns = 
            if resultType <> ResultType.Maps 
            then this.GetOutputColumns(conn, commandText, commandType, sqlParameters)
            else []

        this.AddExecuteMethod(allParametersOptional, sqlParameters, executeArgs, outputColumns, providedCommandType, resultType, singleRow, commandText) 
        
        let getSqlCommandCopy = ProvidedMethod("AsSqlCommand", [], typeof<SqlCommand>)
        getSqlCommandCopy.InvokeCode <- fun args ->
            <@@
                let self : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                let clone = new SqlCommand(self.CommandText, new SqlConnection(self.Connection.ConnectionString), CommandType = self.CommandType)
                clone.Parameters.AddRange <| [| for p in self.Parameters -> SqlParameter(p.ParameterName, p.SqlDbType) |]
                clone
            @@>
        providedCommandType.AddMember getSqlCommandCopy          

        providedCommandType

    member internal this.GetOutputColumns(connection, commandText, commandType, sqlParameters) = 
        try
            this.GetFullQualityColumnInfo(connection, commandText) 
        with :? SqlException as why ->
            try 
                this.FallbackToSETFMONLY(connection, commandText, commandType, sqlParameters) 
            with :? SqlException ->
                raise why

    member internal __.GetFullQualityColumnInfo(connection, commandText) = [
        use cmd = new SqlCommand("sys.sp_describe_first_result_set", connection, CommandType = CommandType.StoredProcedure)
        cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
        use reader = cmd.ExecuteReader()

        while reader.Read() do
            yield { 
                Column.Name = string reader.["name"]
                Ordinal = unbox reader.["column_ordinal"]
                TypeInfo = reader.["system_type_id"] |> unbox |> findTypeInfoBySqlEngineTypeId
                IsNullable = unbox reader.["is_nullable"]
                MaxLength = reader.["max_length"] |> unbox<int16> |> int
            }
    ] 

    member internal __.FallbackToSETFMONLY(connection, commandText, commandType, sqlParameters) = 
        use cmd = new SqlCommand(commandText, connection, CommandType = commandType)
        for p in sqlParameters do
            cmd.Parameters.Add(p.Name, p.TypeInfo.SqlDbType) |> ignore
        use reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly)
        match reader.GetSchemaTable() with
        | null -> []
        | columnSchema -> 
            [
                for row in columnSchema.Rows do
                    yield { 
                        Column.Name = unbox row.["ColumnName"]
                        Ordinal = unbox row.["ColumnOrdinal"]
                        TypeInfo =
                            let t = Enum.Parse(typeof<SqlDbType>, string row.["ProviderType"]) |> unbox
                            findTypeInfoByProviderType(unbox t, "").Value
                        IsNullable = unbox row.["AllowDBNull"]
                        MaxLength = unbox row.["ColumnSize"]
                    }
            ]
        
    member internal this.ExtractSqlParameters(connection, commandText, commandType) =  [

            match commandType with 

            | CommandType.StoredProcedure ->
                //quick solution for now. Maybe better to use conn.GetSchema("ProcedureParameters")
                use cmd = new SqlCommand(commandText, connection, CommandType = CommandType.StoredProcedure)
                SqlCommandBuilder.DeriveParameters cmd

                let xs = cmd.Parameters |> Seq.cast<SqlParameter> |> Seq.toList
                assert(xs.Head.Direction = ParameterDirection.ReturnValue)
                for p in xs.Tail do
                    if p.Direction <> ParameterDirection.Input then failwithf "Only input parameters are supported. Parameter %s has direction %O" p.ParameterName p.Direction
                    yield p.ToParameter()

                // skip RETURN_VALUE
                //yield xs.Head.ToParameter()

            | CommandType.Text -> 
                use cmd = new SqlCommand("sys.sp_describe_undeclared_parameters", connection, CommandType = CommandType.StoredProcedure)
                cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
                use reader = cmd.ExecuteReader()
                while(reader.Read()) do

                    let paramName = string reader.["name"]
                    let sqlEngineTypeId = unbox<int> reader.["suggested_system_type_id"]

                    let udtName = Convert.ToString(value = reader.["suggested_user_type_name"])
                    let direction = 
                        let output = unbox reader.["suggested_is_output"]
                        let input = unbox reader.["suggested_is_input"]
                        if input && output then ParameterDirection.InputOutput
                        elif output then ParameterDirection.Output
                        else ParameterDirection.Input
                    
                    let typeInfo = 
                        match findBySqlEngineTypeIdAndUdt(sqlEngineTypeId, udtName) with
                        | Some x -> x
                        | None -> failwithf "Cannot map unbound variable of sql engine type %i and UDT %s to CLR/SqlDbType type. Parameter name: %s" sqlEngineTypeId udtName paramName

                    yield { 
                        Name = paramName
                        TypeInfo = typeInfo 
                        Direction = direction 
                    }

            | _ -> failwithf "Unsupported command type: %O" commandType    
    ]

    member internal __.GetExecuteArgsForSqlParameters(providedCommandType, sqlParameters, allParametersOptional) = [
        for p in sqlParameters do
            assert p.Name.StartsWith("@")
            let parameterName = p.Name.Substring 1

            let optionalValue = if allParametersOptional then Some null else None

            let parameterType = 
                if not p.TypeInfo.TableType 
                then
                    p.TypeInfo.ClrType
                else
                    assert(p.Direction = ParameterDirection.Input)
                    let rowType = ProvidedTypeDefinition(p.TypeInfo.UdttName, Some typeof<SqlDataRecord>)
                    providedCommandType.AddMember rowType
                    let parameters, metaData = 
                        [
                            for p in p.TypeInfo.TvpColumns do
                                let name, dbType, maxLength = p.Name, p.TypeInfo.SqlDbTypeId, int64 p.MaxLength
                                let paramMeta = 
                                    match p.TypeInfo.IsFixedLength with 
                                    | Some true -> <@@ SqlMetaData(name, enum dbType) @@>
                                    | Some false -> <@@ SqlMetaData(name, enum dbType, maxLength) @@>
                                    | _ -> failwith "Unexpected"
                                let param = 
                                    if p.IsNullable
                                    then ProvidedParameter(p.Name, p.TypeInfo.ClrType, optionalValue = null)
                                    else ProvidedParameter(p.Name, p.TypeInfo.ClrType)
                                yield param, paramMeta
                        ] |> List.unzip

                    let ctor = ProvidedConstructor(parameters)
                    ctor.InvokeCode <- fun args -> 
                        let values = Expr.NewArray(typeof<obj>, [for a in args -> Expr.Coerce(a, typeof<obj>)])
                        <@@ 
                            let result = SqlDataRecord(metaData = %%Expr.NewArray(typeof<SqlMetaData>, metaData)) 
                            let count = result.SetValues(%%values)
                            Debug.Assert(%%Expr.Value(args.Length) = count, "Unexpected return value from SqlDataRecord.SetValues.")
                            result
                        @@>
                    rowType.AddMember ctor

                    ProvidedTypeBuilder.MakeGenericType(typedefof<_ seq>, [ rowType ])

            yield ProvidedParameter(
                parameterName, 
                parameterType = (if allParametersOptional then typedefof<_ option>.MakeGenericType( parameterType) else parameterType), 
                ?optionalValue = optionalValue
            )
    ]

    member internal this.GetExecuteNonQuery(allParametersOptional, paramInfos)  = 
        let body expr =
            <@@
                async {
                    let sqlCommand = %QuotationsFactory.GetSqlCommandWithParamValuesSet(expr, allParametersOptional, paramInfos)
                    //open connection async on .NET 4.5
                    sqlCommand.Connection.Open()
                    use ensureConnectionClosed = sqlCommand.CloseConnectionOnly()
                    let rowsAffected = sqlCommand.AsyncExecuteNonQuery()
                    return! rowsAffected  
                }
            @@>
        typeof<int>, body

    member internal __.AddExecuteMethod(allParametersOptional, paramInfos, executeArgs, outputColumns, providedCommandType, resultType, singleRow, commandText) = 
        let syncReturnType, executeMethodBody = 
            if resultType = ResultType.Maps then
                this.Maps(allParametersOptional, paramInfos, singleRow)
            else
                if outputColumns.IsEmpty
                then 
                    this.GetExecuteNonQuery(allParametersOptional, paramInfos)
                elif resultType = ResultType.DataTable
                then 
                    this.DataTable(providedCommandType, allParametersOptional, paramInfos, commandText, outputColumns, singleRow)
                else
                    let rowType, executeMethodBody = 
                        if List.length outputColumns = 1
                        then
                            let singleCol = outputColumns.Head
                            let column0Type = singleCol.ClrTypeConsideringNullable
                            column0Type, QuotationsFactory.GetBody("SelectOnlyColumn0", column0Type, allParametersOptional, paramInfos, singleRow, singleCol)
                        else 
                            if resultType = ResultType.Tuples
                            then this.Tuples(allParametersOptional, paramInfos, outputColumns, singleRow)
                            else this.Records(providedCommandType, allParametersOptional, paramInfos, outputColumns, singleRow)
                    let returnType = if singleRow then rowType else ProvidedTypeBuilder.MakeGenericType(typedefof<_ seq>, [ rowType ])
                           
                    returnType, executeMethodBody
                    
        let asyncReturnType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ Async>, [ syncReturnType ])
        let asyncExecute = ProvidedMethod("AsyncExecute", executeArgs, asyncReturnType)
        //asyncExecute.InvokeCode <- fun expr -> QuotationsFactory.MapExecuteArgs(paramInfos, expr) |> executeMethodBody
        asyncExecute.InvokeCode <- executeMethodBody
        providedCommandType.AddMember asyncExecute

        let execute = ProvidedMethod("Execute",  executeArgs, syncReturnType)
        execute.InvokeCode <- fun args ->
            let runSync = ProvidedTypeBuilder.MakeGenericMethod(typeof<Async>.GetMethod("RunSynchronously"), [ syncReturnType ])
            //let callAsync = Expr.Call (Expr.Coerce (args.[0], providedCommandType), asyncExecute, args.Tail)
            let callAsync = executeMethodBody args
            Expr.Call(runSync, [ Expr.Coerce (callAsync, asyncReturnType); Expr.Value option<int>.None; Expr.Value option<CancellationToken>.None ])
        providedCommandType.AddMember execute 

    member internal this.Tuples(allParametersOptional, paramInfos, columns, singleRow) =
        let tupleType = getTupleTypeForColumns columns

        let rowMapper = 
            let values = Var("values", typeof<obj[]>)
            let getTupleType = Expr.Call(typeof<Type>.GetMethod("GetType", [| typeof<string>|]), [ Expr.Value tupleType.AssemblyQualifiedName ])
            Expr.Lambda(values, Expr.Coerce(Expr.Call(typeof<FSharpValue>.GetMethod("MakeTuple"), [ Expr.Var values; getTupleType ]), tupleType))

        tupleType, QuotationsFactory.GetBody("GetTypedSequence", tupleType, allParametersOptional, paramInfos, rowMapper, singleRow, columns)

    member internal this.Records( providedCommandType, allParametersOptional, paramInfos,  columns, singleRow) =
        let recordType = ProvidedTypeDefinition("Record", baseType = Some typeof<obj>, HideObjectMethods = true)
        for col in columns do
            if col.Name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." col.Ordinal

            let property = ProvidedProperty(col.Name, propertyType = col.ClrTypeConsideringNullable)
            property.GetterCode <- fun args -> 
                <@@ 
                    let values : obj[] = %%Expr.Coerce(args.[0], typeof<obj[]>)
                    values.[%%Expr.Value (col.Ordinal - 1)]
                @@>

            recordType.AddMember property

        providedCommandType.AddMember recordType
        let getExecuteBody (args : Expr list) = 
            QuotationsFactory.GetTypedSequence(args, allParametersOptional, paramInfos, <@ fun(values : obj[]) -> box values @>, singleRow, columns)
                         
        upcast recordType, getExecuteBody
    
    member internal this.DataTable(providedCommandType, allParametersOptional, paramInfos, commandText, outputColumns, singleRow) =
        let rowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)
        for col in outputColumns do
            let name = col.Name
            if name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." col.Ordinal

            let propertyType = col.ClrTypeConsideringNullable

            let property = 
                if col.IsNullable 
                then
                    ProvidedProperty(name, propertyType = col.ClrTypeConsideringNullable,
                        GetterCode = QuotationsFactory.GetBody("GetNullableValueFromDataRow", col.TypeInfo.ClrType, name),
                        SetterCode = QuotationsFactory.GetBody("SetNullableValueInDataRow", col.TypeInfo.ClrType, name)
                    )
                else
                    ProvidedProperty(name, propertyType, 
                        GetterCode = (fun args -> <@@ (%%args.[0] : DataRow).[name] @@>),
                        SetterCode = fun args -> <@@ (%%args.[0] : DataRow).[name] <- %%Expr.Coerce(args.[1], typeof<obj>) @@>
                    )

            rowType.AddMember property

        providedCommandType.AddMember rowType

        let body = QuotationsFactory.GetBody("GetTypedDataTable", typeof<DataRow>, allParametersOptional, paramInfos, singleRow)
        let returnType = typedefof<_ DataTable>.MakeGenericType rowType

        returnType, body

    member internal this.Maps(allParametersOptional, paramInfos, singleRow) =
        let readerToMap = 
            <@
                fun(reader : SqlDataReader) -> 
                    Map.ofArray<string, obj> [| 
                        for i = 0 to reader.FieldCount - 1 do
                             if not <| reader.IsDBNull(i) then yield reader.GetName(i), reader.GetValue(i)
                    |]  
            @>

        let getExecuteBody(args : Expr list) = 
            QuotationsFactory.GetRows(args, allParametersOptional, paramInfos, readerToMap, singleRow)
            
        typeof<Map<string, obj> seq>, getExecuteBody

