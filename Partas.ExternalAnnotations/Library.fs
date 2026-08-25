module Partas.ExternalAnnotations

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Text
open System.Text.Json
open System.Xml
open System.Xml.Linq

module XmlDocId =
    let private stripArity (n: string) =
        match n.IndexOf '`' with
        | -1 -> n
        | i -> n.Substring(0, i)
    let private chainOf (ty: Type) =
        let rec up(ty: Type) acc =
            if isNull ty.DeclaringType then ty :: acc else up ty.DeclaringType (ty :: acc)
        up ty []

    let private arityOf (ty: Type) =
        if ty.IsGenericType || ty.IsGenericTypeDefinition
        then ty.GetGenericArguments().Length
        else 0

    let rec typeRef (ty: Type): string =
        match ty with
        | ty when ty.IsGenericParameter && isNull ty.DeclaringMethod -> "`" + string ty.GenericParameterPosition
        | ty when ty.IsGenericParameter -> "``" + string ty.GenericParameterPosition
        | ty when ty.IsByRef -> (ty.GetElementType() |> typeRef) + "@"
        | ty when ty.IsPointer -> (ty.GetElementType() |> typeRef) + "*"
        | ty when ty.IsArray && ty.GetArrayRank() = 1 -> (ty.GetElementType() |> typeRef) + "[]"
        | ty when ty.IsArray ->
            (typeRef (ty.GetElementType()))
            + "["
            + (
                Array.create (ty.GetArrayRank()) "0:"
                |> String.concat ","
            )
            + "]"
        | ty ->
            let args = ty.GetGenericArguments()
            let stringBuilder = StringBuilder()

            if not (String.IsNullOrEmpty ty.Namespace)
            then stringBuilder.Append(ty.Namespace).Append('.') |> ignore

            let mutable consumed = 0
            chainOf ty
            |> List.iteri (fun i ty ->
                if i > 0 then stringBuilder.Append('.') |> ignore
                stringBuilder.Append(stripArity ty.Name) |> ignore
                let total = arityOf ty
                if total > consumed then
                    let slice = args[consumed .. total - 1]
                    stringBuilder.Append('{').Append(slice |> Array.map typeRef |> String.concat ",").Append('}') |> ignore
                    consumed <- total
                )
            stringBuilder.ToString()

    /// Used for T: ids and for declaring type prefix of members
    let typeDecl (ty: Type): string =
        if ty.IsGenericParameter || ty.IsArray || ty.IsByRef || ty.IsPointer then typeRef ty else

        let stringBuilder = StringBuilder()

        if not (String.IsNullOrEmpty ty.Namespace)
        then stringBuilder.Append(ty.Namespace).Append('.') |> ignore

        let mutable consumed = 0
        chainOf ty
        |> List.iteri (fun i ty ->
            if i > 0 then stringBuilder.Append('.') |> ignore

            stringBuilder.Append(stripArity ty.Name) |> ignore

            let total = arityOf ty
            if total > consumed then
                stringBuilder.Append('`').Append(total - consumed) |> ignore
                consumed <- total
            )
        stringBuilder.ToString()

    let private paramList (ps: ParameterInfo[]) =
        if Array.isEmpty ps then "" else
        "("
        + (
            ps
            |> Array.map (_.ParameterType >> typeRef)
            |> String.concat ","
        )
        + ")"

    let private memberName (n: string) =
        n.Replace('.', '#').Replace('<', '{').Replace('>', '}')

    let ofMember (m: MemberInfo): string =
        match m with
        | :? Type as t -> "T:" + typeDecl t
        | _ ->
            let owner = typeDecl m.DeclaringType
            match m with
            | :? FieldInfo -> "F:" + owner + "." + memberName m.Name
            | :? EventInfo -> "E:" + owner + "." + memberName m.Name
            | :? PropertyInfo as p -> "P:" + owner + "." + memberName p.Name + paramList (p.GetIndexParameters())
            | :? MethodBase as methodBase ->
                let name =
                    if not methodBase.IsConstructor then memberName methodBase.Name
                    elif methodBase.IsStatic then "#cctor"
                    else "#ctor"
                let generic =
                    if not(methodBase.IsGenericMethod || methodBase.IsGenericMethodDefinition) then "" else
                    "``" + string (methodBase.GetGenericArguments().Length)
                let ret =
                    match methodBase with
                    | :? MethodInfo as methodInfo when methodInfo.Name = "op_Implicit" || methodInfo.Name = "op_Explicit" ->
                        "~" + typeRef methodInfo.ReturnType
                    | _ -> ""
                "M:" + owner
                + "." + name + generic
                + paramList (methodBase.GetParameters())
                + ret
            | _ -> failwithf $"Unsupported member kind: %A{m.MemberType}"

module private Dependency =
    let probe (dll: string) =
        let deps = Path.ChangeExtension(dll, ".deps.json")
        if not (File.Exists deps) then [] else

        let nugetRoot =
            let envVar = Environment.GetEnvironmentVariable "NUGET_PACKAGES"
            if not <| String.IsNullOrEmpty envVar then envVar else
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")

        use doc = JsonDocument.Parse(File.ReadAllText deps)
        [
            for target in doc.RootElement.GetProperty("targets").EnumerateObject() do
            for package in target.Value.EnumerateObject() do
            match package.Value.TryGetProperty "runtime" with
            | true, rt ->
                match package.Name.Split '/' with
                | [| name; version |] ->
                    for file in rt.EnumerateObject() do
                        let path = Path.Combine(nugetRoot, name.ToLowerInvariant(), version, file.Name)
                        if File.Exists path then yield Path.GetDirectoryName path
                | _ -> ()
            | _ -> ()
        ]

module private AttributeValues =
    let enumName (ty: Type) (v: obj) =
        ty.GetFields(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.tryFind (fun f ->
            try Convert.ToInt64(f.GetRawConstantValue()) = Convert.ToInt64(v)
            with _ -> false
            )
        |> Option.map _.Name
        |> Option.defaultValue (string v)
    let rec fmtValue (arg: CustomAttributeTypedArgument) =
        match arg.Value with
        | null -> ""
        | :? Collections.Generic.IList<CustomAttributeTypedArgument> as items -> items |> Seq.map fmtValue |> String.concat ","
        | :? Type as ty -> XmlDocId.typeDecl ty
        | v when arg.ArgumentType.IsEnum -> enumName arg.ArgumentType v
        | v -> string v


type private NameProvider(resolveName: string -> Type) =
    interface ICustomAttributeTypeProvider<string> with
        member _.GetPrimitiveType(code) = string code
        member _.GetSystemType() = "System.Type"
        member _.IsSystemType(ty) = ty = "System.Type"
        member _.GetTypeFromSerializedName(name) = name
        member _.GetSZArrayType(ele) = ele + "[]"
        member _.GetTypeFromDefinition(reader, handle, _) =
            let typeDefinition = reader.GetTypeDefinition handle
            reader.GetString typeDefinition.Namespace
            + "."
            + reader.GetString typeDefinition.Name
        member _.GetTypeFromReference(reader, handle, _) =
            let typeReference = reader.GetTypeReference handle
            reader.GetString typeReference.Namespace
            + "."
            + reader.GetString typeReference.Name
        member _.GetUnderlyingEnumType(ty) =
            match resolveName ty with
            | null -> PrimitiveTypeCode.Int32
            | ty ->
                match ty.GetEnumUnderlyingType().Name with
                | nameof PrimitiveTypeCode.Byte -> PrimitiveTypeCode.Byte
                | nameof PrimitiveTypeCode.SByte -> PrimitiveTypeCode.SByte
                | nameof PrimitiveTypeCode.Int16 -> PrimitiveTypeCode.Int16
                | nameof PrimitiveTypeCode.UInt16 -> PrimitiveTypeCode.UInt16
                | nameof PrimitiveTypeCode.Int32 -> PrimitiveTypeCode.Int32
                | nameof PrimitiveTypeCode.UInt32 -> PrimitiveTypeCode.UInt32
                | nameof PrimitiveTypeCode.Int64 -> PrimitiveTypeCode.Int64
                | nameof PrimitiveTypeCode.UInt64 -> PrimitiveTypeCode.UInt64
                | _ -> PrimitiveTypeCode.Int32

/// <summary>
/// Which attributes a generation run collects. An assembly's whole JetBrains annotation surface is
/// worth shipping, so the default is every attribute in that namespace; narrowing it is an opt-in.
/// </summary>
[<RequireQualifiedAccess>]
type AttributeFilter =
    /// <summary>Every attribute declared in the <c>JetBrains.Annotations</c> namespace.</summary>
    | JetBrains
    /// <summary>Only attributes whose simple name appears here, whatever namespace declares them.</summary>
    | Named of names: string list
    /// <summary>Arbitrary predicate over an attribute type's namespace and simple name.</summary>
    | Where of predicate: (string -> string -> bool)

module AttributeFilter =
    [<Literal>]
    let JetBrainsNamespace = "JetBrains.Annotations"

    /// <summary>
    /// Flattens a filter into the predicate both matching paths use: the reflected one over
    /// <c>CustomAttributeData</c>, and the raw metadata one that reads the attribute blob.
    /// </summary>
    /// <remarks>
    /// Both must agree, because a hit is located in the blob by its index among the matches at that
    /// site; disagreeing predicates would read another attribute's named arguments.
    /// </remarks>
    let predicate (filter: AttributeFilter): string -> string -> bool =
        match filter with
        | AttributeFilter.JetBrains ->
            fun ns _ -> ns = JetBrainsNamespace
        | AttributeFilter.Named names ->
            let names = Set.ofList names
            fun _ -> names.Contains
        | AttributeFilter.Where predicate -> predicate

type private Site =
    | OnMember
    | OnParameter of ParameterInfo
    | OnReturn of ParameterInfo
    /// A generic parameter of the owning type or method; the Type here is the parameter itself.
    | OnTypeParameter of Type

/// <summary>
/// Identifies the element a hit is emitted into, so several attributes on one site share a single
/// <c>parameter</c> or <c>return</c> element rather than getting one each.
/// </summary>
[<RequireQualifiedAccess>]
type private SiteKey =
    | Member
    | Return
    | Parameter of name: string
    | TypeParameter of name: string

type private Hit = {
    Owner: MemberInfo
    Site: Site
    Attribute: CustomAttributeData
    Index: int
}

module private Hit =
    let siteToken (hit: Hit) =
        match hit.Site with
        | OnMember -> hit.Owner.MetadataToken
        | OnParameter p | OnReturn p -> p.MetadataToken
        | OnTypeParameter tp -> tp.MetadataToken

type ExternalAnnotationGenerator(targetAssemblyDll: string, ?filter: AttributeFilter, ?depsProbeDirs) =
    let matchesName = AttributeFilter.predicate (defaultArg filter AttributeFilter.JetBrains)
    let depsProbeDirectories =
        [
            yield Path.GetDirectoryName targetAssemblyDll
            yield! defaultArg depsProbeDirs []
            yield! (Dependency.probe targetAssemblyDll)
            yield Path.GetDirectoryName typeof<obj>.Assembly.Location
        ]
        |> List.filter Directory.Exists
        |> List.distinct
    let paths =
        depsProbeDirectories
        |> Seq.collect (fun d -> Directory.EnumerateFiles(d, "*.dll"))
        |> Seq.distinctBy (Path.GetFileNameWithoutExtension >> _.ToLowerInvariant())
        |> Seq.toArray
    let mlc = new MetadataLoadContext(PathAssemblyResolver paths)
    let asm = mlc.LoadFromAssemblyPath targetAssemblyDll
    let allTypes =
        try
            asm.GetTypes()
        with :? ReflectionTypeLoadException as ex ->
            ex.Types |> Array.filter (isNull >> not)
    let peStream = File.OpenRead targetAssemblyDll
    let peReader = new PEReader(peStream)
    let mdReader = peReader.GetMetadataReader()
    let resolveName (n: string) =
        asm.GetType(n.Split(',')[0])
    /// Namespace and simple name of the attribute a raw metadata blob constructs.
    let blobCtorTypeName (customAttr: CustomAttribute) =
        let ofDefinition (handle: TypeDefinitionHandle) =
            let typeDefinition = mdReader.GetTypeDefinition handle
            mdReader.GetString typeDefinition.Namespace, mdReader.GetString typeDefinition.Name
        match customAttr.Constructor.Kind with
        | HandleKind.MethodDefinition ->
            let methodDef = mdReader.GetMethodDefinition(MethodDefinitionHandle.op_Explicit customAttr.Constructor)
            ofDefinition (methodDef.GetDeclaringType())
        | HandleKind.MemberReference ->
            let memberRef = mdReader.GetMemberReference(MemberReferenceHandle.op_Explicit customAttr.Constructor)
            match memberRef.Parent.Kind with
            | HandleKind.TypeReference ->
                let typeReference = mdReader.GetTypeReference(TypeReferenceHandle.op_Explicit memberRef.Parent)
                mdReader.GetString typeReference.Namespace, mdReader.GetString typeReference.Name
            | HandleKind.TypeDefinition ->
                ofDefinition (TypeDefinitionHandle.op_Explicit memberRef.Parent)
            | _ -> "", ""
        | _ -> "", ""
    let fmtRaw (typeName: string) (v: obj) =
        match v with
        | null -> ""
        | v ->
            match resolveName typeName with
            | null -> string v
            | ty when ty.IsEnum -> AttributeValues.enumName ty v
            | _ -> string v
    let blobNamedArgs (token: int) (index: int) =
        let matching =
            mdReader.GetCustomAttributes(MetadataTokens.EntityHandle token)
            |> Seq.map mdReader.GetCustomAttribute
            |> Seq.filter (blobCtorTypeName >> (fun (ns, name) -> matchesName ns name))
            |> Seq.toArray
        if index >= matching.Length then [] else
        let decoded = matching[index].DecodeValue(NameProvider(resolveName))
        [ for n in decoded.NamedArguments -> n.Name, fmtRaw n.Type n.Value ]
    let namedArgsOf (hit: Hit) =
        try [ for n in hit.Attribute.NamedArguments -> n.MemberName, AttributeValues.fmtValue n.TypedValue ]
        with :? ArgumentNullException -> blobNamedArgs (Hit.siteToken hit) hit.Index
    let matches (cad: CustomAttributeData) =
        let ty = cad.AttributeType
        matchesName (if isNull ty.Namespace then "" else ty.Namespace) ty.Name
    let hitsOn (site: Site) (owner: MemberInfo) (attrs: CustomAttributeData seq) =
        attrs
        |> Seq.filter matches
        |> Seq.mapi (fun i a ->
            {
                Owner = owner
                Site = site
                Attribute = a
                Index = i
            })
    let declared = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly
    let paramHits owner (ps: ParameterInfo[]) =
        ps
        |> Seq.collect (fun p -> hitsOn (OnParameter p) owner (p.GetCustomAttributesData()))
    /// <summary>
    /// Generic parameters declared by an owner itself.
    /// </summary>
    /// <remarks>
    /// A nested type <b>redeclares</b> every enclosing type's generic parameters as its own, with
    /// their attributes copied onto the redeclaration, so <c>DeclaringType</c> cannot tell an
    /// inherited parameter from an owned one - only position can. The first <c>skip</c> of them
    /// belong to the enclosing type; emitting those would repeat the outer type's annotations on
    /// every nested type, which is not what the source declared.
    /// </remarks>
    let typeParamHits (owner: MemberInfo) (skip: int) (ps: Type[]) =
        ps
        |> Seq.filter (fun tp -> tp.GenericParameterPosition >= skip)
        |> Seq.collect (fun tp -> hitsOn (OnTypeParameter tp) owner (tp.GetCustomAttributesData()))
    let hitsIn (ty: Type) =
        [|
            yield! hitsOn OnMember ty (ty.GetCustomAttributesData())
            if ty.IsGenericTypeDefinition then
                let inherited =
                    if ty.IsNested && ty.DeclaringType.IsGenericTypeDefinition then
                        ty.DeclaringType.GetGenericArguments().Length
                    else 0
                yield! typeParamHits ty inherited (ty.GetGenericArguments())
            for member' in ty.GetMembers declared do
            if not (member' :? Type) then
                yield! hitsOn OnMember member' (member'.GetCustomAttributesData())
                match member' with
                | :? MethodBase as methodBase ->
                    yield! paramHits member' (methodBase.GetParameters())
                    // A method's own parameters are all it reports; none of its types leak in.
                    if methodBase.IsGenericMethodDefinition then
                        yield! typeParamHits member' 0 (methodBase.GetGenericArguments())
                    match methodBase with
                    | :? MethodInfo as methodInfo ->
                        yield! hitsOn (OnReturn methodInfo.ReturnParameter) member' (methodInfo.ReturnParameter.GetCustomAttributesData())
                    | _ -> ()
                | :? PropertyInfo as prop -> yield! paramHits member' (prop.GetIndexParameters())
                | _ -> ()
        |]
    let mutable skipped = []
    let hits =
        allTypes
        |> Array.collect (fun ty ->
            try hitsIn ty
            with e ->
                skipped <- (ty.FullName, e.Message) :: skipped
                [||]
            )
    let docIdOf (memberInfo: MemberInfo) =
        try XmlDocId.ofMember memberInfo |> Ok
        with e -> $"{memberInfo.DeclaringType}.{memberInfo.Name}: {e.Message}" |> Error
    let byMember =
        hits
        |> Array.groupBy _.Owner
        |> Array.choose (fun (owner, innerHits) ->
            match docIdOf owner with
            | Ok id -> Some(id, innerHits)
            | Error msg ->
                skipped <- (msg, "doc id") :: skipped
                None
            )
        |> Array.sortBy fst
    let describe (hit: Hit) =
        let ctor =
            match docIdOf hit.Attribute.Constructor with
            | Ok id -> id
            | Error e -> e
        let args =
            hit.Attribute.ConstructorArguments
            |> Seq.map (AttributeValues.fmtValue >> sprintf "%A")
            |> String.concat ", "
        let props =
            namedArgsOf hit
            |> Seq.map (fun (name, value) -> $"%s{name}=%A{value}")
            |> String.concat " "
        sprintf "%s(%s)%s" ctor args (if props = "" then "" else "  " + props)
    let xmlFor (docId: string) (hits: Hit[]) =
        let attrXml (hit: Hit) =
            let ctor =
                match docIdOf hit.Attribute.Constructor with
                | Ok id -> id
                | Error _ -> "?"
            let ele = XElement("attribute")
            ele.SetAttributeValue("ctor", ctor)
            let content = [|
                for arg in hit.Attribute.ConstructorArguments do
                    let argument = XElement("argument")
                    argument.SetValue(AttributeValues.fmtValue arg)
                    yield argument
                for name, value in namedArgsOf hit do
                    let prop = XElement("property")
                    prop.SetAttributeValue("name", name)
                    prop.SetValue(value)
                    yield prop
            |]
            ele.Add(content)
            ele
        let member' = XElement("member")
        member'.SetAttributeValue("name", docId)
        // Grouped by site: a parameter carrying two annotations gets one element with two
        // children, which is what all but ~0.5% of ReSharper's own shipped files do. Array.groupBy
        // keeps first-appearance order, so member attributes still precede parameters.
        let hitElements =
            hits
            |> Array.groupBy (fun hit ->
                match hit.Site with
                | OnMember -> SiteKey.Member
                | OnReturn _ -> SiteKey.Return
                | OnParameter para -> SiteKey.Parameter (if isNull para.Name then "" else para.Name)
                | OnTypeParameter tp -> SiteKey.TypeParameter tp.Name
                )
            |> Array.collect (fun (site, hits) ->
                match site with
                | SiteKey.Member -> hits |> Array.map attrXml
                | SiteKey.Return ->
                    let ret = XElement("return")
                    ret.Add(hits |> Array.map attrXml)
                    [| ret |]
                | SiteKey.Parameter name ->
                    let paraEle = XElement("parameter")
                    paraEle.SetAttributeValue("name", name)
                    paraEle.Add(hits |> Array.map attrXml)
                    [| paraEle |]
                | SiteKey.TypeParameter name ->
                    let typeParaEle = XElement("typeparameter")
                    typeParaEle.SetAttributeValue("name", name)
                    typeParaEle.Add(hits |> Array.map attrXml)
                    [| typeParaEle |]
                )
        member'.Add(hitElements)
        member'

    do
        if not (List.isEmpty skipped) then
            eprintfn $"\n%d{skipped.Length} skipped:"
            for name, msg in List.truncate 10 skipped do
                eprintfn $"   %s{name} -- %s{msg}"

    member this.GenerateXml() =
        let assemblyEle = XElement("assembly")
        assemblyEle.SetAttributeValue("name", asm.GetName().Name)
        byMember
        |> Array.map (fun (docId, hits) -> xmlFor docId hits)
        |> assemblyEle.Add
        let doc = XDocument(assemblyEle)
        doc.Declaration <- XDeclaration("1.0", "utf-8", null)
        doc
    member _.TypeScanCount = allTypes.Length
    member _.SiteCount = hits.Length
    member _.MemberCount = byMember.Length
    member _.Skipped = skipped
    member _.PrintfMembers() =
        byMember
        |> Array.iter (fun (docId, hits) ->
            printfn $"%s{docId}"
            for hit in hits do
                let site =
                    match hit.Site with
                    | OnMember -> "member"
                    | OnParameter p -> "parameter " + p.Name
                    | OnReturn _ -> "return"
                    | OnTypeParameter tp -> "typeparameter " + tp.Name
                printfn $"    %-14s{site} %s{describe hit}"
            )

    interface IDisposable with
        member _.Dispose() =
            peReader.Dispose()
            peStream.Dispose()
            mlc.Dispose()

/// <summary>
/// Counts from a generation run. <c>Skipped</c> pairs a type or member with the reason no
/// annotation could be emitted for it; a non-empty list means those sites are silently absent
/// from the output.
/// </summary>
type GenerateResult =
    { Types: int
      Sites: int
      Members: int
      Skipped: (string * string) list }

/// <summary>
/// Scans <paramref name="assembly"/> for the attributes <paramref name="filter"/> selects and
/// writes a ReSharper external annotations file to <paramref name="output"/>, creating its
/// directory if needed.
/// </summary>
/// <param name="filter">Which attributes to collect. <c>AttributeFilter.JetBrains</c> takes the
/// whole <c>JetBrains.Annotations</c> namespace; the other cases narrow that.</param>
/// <param name="probeDirs">Extra directories to resolve the assembly's references from, on top of
/// its own directory, its <c>deps.json</c> closure and the host's shared framework.</param>
/// <param name="assembly">The assembly to scan.</param>
/// <param name="output">The file to write to.</param>
let generateWith (filter: AttributeFilter) (probeDirs: string list) (assembly: string) (output: string) =
    use generator = new ExternalAnnotationGenerator (assembly, filter, probeDirs)
    let doc = generator.GenerateXml ()

    match Path.GetDirectoryName output with
    | null | "" -> ()
    | dir -> Directory.CreateDirectory dir |> ignore

    // Written through an explicit writer rather than doc.Save, which emits a UTF-8 BOM and so
    // makes the file differ byte-for-byte from an otherwise identical one.
    let settings = XmlWriterSettings (Indent = true, IndentChars = "  ", Encoding = UTF8Encoding false)
    use writer = XmlWriter.Create (output, settings)
    doc.Save writer
    writer.Flush ()

    { Types = generator.TypeScanCount
      Sites = generator.SiteCount
      Members = generator.MemberCount
      Skipped = generator.Skipped }

/// <summary>
/// Scans <paramref name="assembly"/> for every <c>JetBrains.Annotations</c> attribute and writes a
/// ReSharper external annotations file to <paramref name="output"/>.
/// </summary>
/// <remarks>
/// The whole namespace is taken deliberately: a consumer reading the sidecar wants the assembly's
/// annotation surface, not one attribute of it. Use <see cref="M:Partas.ExternalAnnotations.generateWith"/>
/// with <c>AttributeFilter.Named</c> to narrow it.
/// </remarks>
/// <param name="assembly">The assembly to scan.</param>
/// <param name="output">The file to write to.</param>
let generate (assembly: string) (output: string) =
    generateWith AttributeFilter.JetBrains [] assembly output
