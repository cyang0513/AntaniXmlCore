﻿namespace AntaniXmlCore

///This module provides generators supporting the facets defined in XML schema.

//For example the `pattern` facet is supported using a [regex based generator](https://github.com/moodmosaic/Fare).
//Much easier is the one for `enum` facets, yielding a generator for a fixed set of values.
//For text based data types we have generators of bounded length values according 
//to `Length`, `MinLength` and `MaxLength` facets.
//And for data types that can be sorted there is a generator of values restricted to a
//certain interval dictated by facets like `MinInclusive`, `MaxExclusive`...


module FacetBasedGenerators =
    open System
    open System.Xml
    open System.Text.RegularExpressions
    open FsCheck
    open Fare
    open XsdDomain
    open LexicalMappings
    open ConstrainedGenerators


    // generate random strings from regex
    // https://github.com/moodmosaic/Fare 
    // known limitations:
    // the character classes \i and \c are specific to W3C XML Schema (hence are not supported)
    

    let xeger pattern =
        //TODO better handling of issues
        let isSupported = 
            [ ".*.*"
              ".*:.*"
              "([0-9]+|[0-9]+.[0-9]*|[0-9]*.[0-9]+)Hz"
            ] 
            |> List.forall ((<>) pattern)
        if isSupported then 
            let x = Xeger(pattern)
            gen {return x.Generate() }
        else Gen.constant ("unsupported pattern " + pattern)


    let genPattern patterns = 
        // xsd allows multiple patterns in the same simpleType definition
        // valid values must conform to at least one pattern, but not necessarily to all
        patterns 
        |> List.map xeger
        |> Gen.oneof

    let inline isMatch (inp, pat) =
        let ret = Regex.IsMatch(inp, sprintf "^%s$" pat)
        //printfn "'%s' %s %s" inp (if ret then "matches" else "DO NOT matches") pat
        ret;

    let hasPattern patterns inp =
        patterns
        |> List.exists (fun pat -> isMatch(inp, pat))

    let patternGen facets = 
        facets.Patterns 
        |> List.filter(fun x -> not x.IsEmpty)
        |> List.map (fun x -> Some { Gen  = genPattern x
                                     Description = sprintf "pattern %A" x
                                     Prop = hasPattern x })
            
    let enumGen facets =
        match facets.Enumeration with
        | [] -> None
        | items -> 
            Some { Gen  = Gen.elements items
                   Description = sprintf "enum %A" items
                   Prop = fun x -> items |> List.exists ((=) x) }
          
   
          
    let lengthBounds facets defaultMin defaultMax = 
        match facets.Length, facets.MinLength, facets.MaxLength with
        | None, None, None -> defaultMin, defaultMax
        | Some len, None, None -> len, len
        | None, Some min, None -> min, defaultMax
        | None, None, Some max -> defaultMin, max
        | None, Some min, Some max -> min, max
        | x -> failwithf "Unexpected combination of facets %A" x

    
                
    let commonChars = 
        Arb.Default.Char().Generator 
        |> Gen.suchThat XmlConvert.IsXmlChar

    let allXmlChars = 
        [ Char.MinValue .. Char.MaxValue]
        |> List.filter XmlConvert.IsXmlChar 
        |> Gen.elements

    // once in a while use the whole character set
    let genXmlChar = Gen.frequency [100, commonChars; 2, allXmlChars]

    /// Creates a generator of string values of bounded length
    let boundedStringGen facets defaultMin defaultMax =

        let generator (minLen, maxLen) =
            gen { let! len = Gen.choose(minLen, maxLen)
                  let! chars = genXmlChar |> Gen.arrayOfLength len
                  return System.String(chars) }

        let isBoundedString (minLen, maxLen) str = 
            let len = String.length str
            minLen <= len && len <= maxLen

        let v = lengthBounds facets defaultMin defaultMax
        { Gen = generator v
          Description = sprintf "length %A" v
          Prop = isBoundedString v }

    /// Creates a generator of values constrained to an interval determined by facets
    /// or by the default min and max provided. Epsilon allows adjusting bounds for
    /// MinExclusive and MaxExclusive facets. A lexical mapping is also needed to
    /// parse facets values and to format the generated values
    let inline boundedGen baseGen lex facets defaultMin defaultMax (epsilon: ^a) =
        let lowerConstraint, minValue =
            match facets.MinInclusive, facets.MinExclusive with
            | Some x, None -> let v = lex.Parse x in (fun x -> x >= v), v
            | None, Some x -> let v = lex.Parse x in (fun x -> x >  v), v + epsilon
            | _            -> let v = defaultMin  in (fun x -> x >= v), v
        let upperConstraint, maxValue =
            match facets.MaxInclusive, facets.MaxExclusive with
            | Some x, None -> let v = lex.Parse x in (fun x -> x <= v), v
            | None, Some x -> let v = lex.Parse x in (fun x -> x <  v), v - epsilon
            | _            -> let v = defaultMax  in (fun x -> x <= v), v
        // instead of failing we may decrease epsilon?
        if (minValue > maxValue) then failwith "range is too narrow"

        let forceBounds x =
            if   not (lowerConstraint x) then minValue
            elif not (upperConstraint x) then maxValue
            else x

        { Gen = baseGen 
          Description = "boundedGen" // todo better description
          Prop = fun x -> lowerConstraint x && upperConstraint x }
        |> map forceBounds
        |> lexMap lex
       
        
    let denormalize stringGenerator = gen {
        let! x = stringGenerator |> Gen.suchThat isNormalized
        if x.Contains " " then
            let spaceIndexes = 
                [0..x.Length-1] |> List.filter (fun i -> x.[i] = ' ')
            let! spaces =
                Gen.frequency [
                    1, Gen.elements [' '; '\n'; '\r';'\t']
                    20, Gen.constant ' ' ]
                |> Gen.listOfLength spaceIndexes.Length
            let d = List.zip spaceIndexes spaces |> dict
            let ret =
                x
                |> Seq.mapi (fun i c -> 
                        match d.TryGetValue i with
                        | true, s -> s
                        | _ -> c)
                |> Seq.toArray
            return System.String(ret) 
        else return x }

    let expand stringGenerator =
        // char list to string
        let str = Array.ofList >> fun x -> System.String(x)
        let spaceCharGen = Gen.elements [' '; '\n'; '\r';'\t']
        let outerGen = 
            Gen.frequency 
                [ 20, Gen.constant ""
                  1, spaceCharGen |> Gen.listOf |> Gen.map str ]
        let innerGen = 
            Gen.frequency 
                [ 20, Gen.constant " "
                  1, spaceCharGen |> Gen.nonEmptyListOf |> Gen.map str ]
        gen { 
            let! x = stringGenerator |> Gen.suchThat isCollapsed
            match x.Split [|' '|] |> List.ofArray with
            | [] -> return x
            | head::tail ->
                let! prefix = outerGen
                let! suffix = outerGen
                let! spaces = innerGen |> Gen.listOfLength tail.Length
                let chars = seq {
                    yield prefix
                    yield head
                    for t, s in Seq.zip tail spaces do
                        yield t
                        yield s 
                    yield suffix } |> Array.ofSeq
                return String.Concat chars }


    let handleWhitespace = function
        | Preserve -> id
        | Replace  -> denormalize
        | Collapse -> expand

    /// Combines the given generator with other ones based on pattern
    /// and enum facets (if present). Also apply the whitespace handling
    /// specified in the facets (if present) or the default one provided
    let applyTextFacets facets whitespaceHandling generator =
        let wsh = defaultArg (facets.WhiteSpace) whitespaceHandling
        patternGen facets @
        [ 
          enumGen facets
          Some generator ] 
        |> List.choose id
        |> mix
        |> handleWhitespace wsh 

    /// this version is to handle a corner case with token:
    /// it seems that a token with only spaces is not collapsed
    /// by the .NET validator resulting in validation error
    let applyTextFacets' facets _ generator =
        patternGen facets @
        [ 
          enumGen facets
          Some generator ] 
        |> List.choose id
        |> mix
        |> Gen.suchThat isCollapsed

