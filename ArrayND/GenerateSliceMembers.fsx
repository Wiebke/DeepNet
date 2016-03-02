﻿
open System
open System.IO

let maxDim = 3

let f = new StreamWriter ("ArrayNDSliceMembers.txt")
let ws = "        "

let prn fmt =
    let write str = 
        f.WriteLine (ws + str)
    Printf.kprintf write fmt


type ModeT =
    | IntValue
    | Special
    | Range

prn "// ========================= SLICE MEMBERS BEGIN ============================="

for dim in 1 .. maxDim do
    let ad = {0 .. dim-1}

    let rec generate modes = 
        if List.length modes < dim then
            generate (IntValue :: modes)
            generate (Special :: modes)
            generate (Range :: modes)
        else
            let decls, calls, itemCalls = 
                modes 
                |> List.mapi (fun dim mode ->
                    match mode with
                    | IntValue -> 
                        sprintf "d%d: int" dim, 
                        sprintf "SliceElem d%d" dim,
                        sprintf "d%d" dim
                    | Special -> 
                        sprintf "d%d: SpecialAxisT" dim, 
                        sprintf "SliceSpecial d%d" dim,
                        ""
                    | Range -> 
                        sprintf "d%ds: int option, d%df: int option" dim dim,
                        sprintf "SliceRng (d%ds, d%df)" dim dim,
                        "")
                |> List.unzip3

            if List.contains Range modes then
                prn "member inline this.GetSlice (%s) = " (String.concat ", " decls)
                prn "     getSliceView [%s] this" (String.concat "; " calls)
                prn "member inline this.SetSlice (%s, value: ArrayNDT<'T>) = " (String.concat ", " decls)
                prn "     setSliceView [%s] this value" (String.concat "; " calls)
            elif List.contains Special modes then
                prn "member inline this.Item"
                prn "    with get (%s) = " (String.concat ", " decls)
                prn "        getSliceView [%s] this" (String.concat "; " calls)
                prn "    and set (%s) value = " (String.concat ", " decls)
                prn "        setSliceView [%s] this value" (String.concat "; " calls)
            else
                prn "member inline this.Item"
                prn "    with get (%s) = " (String.concat ", " decls)
                prn "        this.[[%s]]" (String.concat "; " itemCalls)
                prn "    and set (%s) value = " (String.concat ", " decls)
                prn "        this.[[%s]] <- value" (String.concat "; " itemCalls)

    prn ""
    prn "// %d dimensions" dim
    generate []


prn "// ========================= SLICE MEMBERS END ==============================="

f.Dispose ()


        