/// <summary>
/// The file <c>generateWith</c> writes, as bytes rather than as a document.
/// </summary>
/// <remarks>
/// This output is committed, diffed and packed. A byte-order mark or an unstable member order makes
/// every regeneration a diff, which is how a stale sidecar stops being noticed.
/// </remarks>
module Partas.ExternalAnnotationsTests.OutputTests

open System
open System.IO
open Expecto
open Partas.ExternalAnnotations
open Partas.ExternalAnnotationsTests.Helpers

/// A temporary directory that does not exist yet, so a test can watch it be created.
let private inTempDirectory (body: string -> 'a) : 'a =
    let root = Path.Combine (Path.GetTempPath (), "partas-annotations-tests", Guid.NewGuid().ToString "N")

    try
        body root
    finally
        try
            Directory.Delete (root, true)
        with _ ->
            ()

[<Tests>]
let tests =
    testList "output" [
        test "no byte-order mark" {
            generatingFixture (fun generated ->
                let bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

                // XDocument.Save writes one; an explicit writer is used to avoid it, so that two
                // otherwise identical files compare equal byte for byte.
                Expect.notEqual (generated.Bytes |> Array.truncate 3) bom "the file should not start with a UTF-8 BOM")
        }

        test "declares utf-8" {
            generatingFixture (fun generated ->
                Expect.stringStarts generated.Text "<?xml version=\"1.0\" encoding=\"utf-8\"?>" "the declaration")
        }

        test "indented two spaces" {
            generatingFixture (fun generated ->
                let lines = generated.Text.Split '\n' |> Array.map (fun line -> line.TrimEnd '\r')

                Expect.contains lines "<assembly name=\"AnnotationsFixture.Cs\">" "the root at column zero"

                Expect.isTrue
                    (lines |> Array.exists (fun line -> line.StartsWith "  <member "))
                    "members indented by two"

                Expect.isTrue
                    (lines |> Array.exists (fun line -> line.StartsWith "    <attribute "))
                    "attributes by four")
        }

        test "members are sorted by doc id" {
            generatingFixture (fun generated ->
                let names = Xml.memberNames generated.Document

                // Sorted, not in reflection order: the order GetTypes and GetMembers return is not
                // promised to be stable, and an unsorted file re-diffs itself on every build.
                Expect.equal names (List.sortWith (fun a b -> String.CompareOrdinal (a, b)) names) "ordinal, ascending"
                Expect.isGreaterThan names.Length 1 "with enough members for the order to mean something")
        }

        test "two runs over one assembly are byte-identical" {
            inTempDirectory (fun root ->
                let first = Path.Combine (root, "first.xml")
                let second = Path.Combine (root, "second.xml")

                let a = generateWith fixtureAttributes [] csharpAssembly first
                let b = generateWith fixtureAttributes [] csharpAssembly second

                Expect.equal a b "the same counts"
                Expect.equal (File.ReadAllBytes first) (File.ReadAllBytes second) "and the same bytes")
        }

        test "the output's directory is created if it is not there" {
            inTempDirectory (fun root ->
                let output = Path.Combine (root, "one", "two", "annotations.xml")
                Expect.isFalse (Directory.Exists root) "the directory should not exist beforehand"

                generateWith fixtureAttributes [] csharpAssembly output |> ignore

                Expect.isTrue (File.Exists output) "the file should have been written into a directory that had to be made")
        }

        test "a bare file name works, needing no directory at all" {
            inTempDirectory (fun root ->
                Directory.CreateDirectory root |> ignore
                let previous = Directory.GetCurrentDirectory ()

                try
                    Directory.SetCurrentDirectory root
                    // GetDirectoryName is "" here, which must not be passed to CreateDirectory.
                    generateWith fixtureAttributes [] csharpAssembly "annotations.xml" |> ignore
                    Expect.isTrue (File.Exists (Path.Combine (root, "annotations.xml"))) "written into the working directory"
                finally
                    Directory.SetCurrentDirectory previous)
        }

        test "an existing file is replaced, not appended to" {
            inTempDirectory (fun root ->
                Directory.CreateDirectory root |> ignore
                let output = Path.Combine (root, "annotations.xml")
                File.WriteAllText (output, String.replicate 100 "stale content that is longer than the real file\n")

                generateWith fixtureAttributes [] csharpAssembly output |> ignore

                Expect.isFalse ((File.ReadAllText output).Contains "stale") "no trace of what was there before"
                Expect.stringStarts (File.ReadAllText output) "<?xml" "and a well-formed file in its place")
        }

        test "the file names the assembly it describes" {
            // ReSharper pairs a sidecar with an assembly by this name; getting it wrong loads a
            // file that annotates nothing.
            generatingFixture (fun generated -> Expect.equal (Xml.assemblyName generated.Document) "AnnotationsFixture.Cs" "the assembly's simple name")
        }
    ]
