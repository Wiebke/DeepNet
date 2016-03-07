﻿namespace SymTensor

open ShapeSpec


[<AutoOpen>]
module TypeNameTypes =

    /// assembly qualified name of a .NET type
    type TypeNameT = TypeName of string


module TypeName =

    /// gets the System.Type associated by this TypeName
    let getType (TypeName tn) =
        System.Type.GetType(tn)

    /// gets the TypeName assoicated with the given type
    let ofType<'T> =
        TypeName (typeof<'T>.AssemblyQualifiedName)

    /// gets the TypeName assoicated with the given System.Type object
    let ofTypeInst (t: System.Type) =
        TypeName t.AssemblyQualifiedName

    /// gets the TypeName assoicated with the given System.Type object
    let ofObject (o: obj) =
        ofTypeInst (o.GetType())


[<AutoOpen>]
module VarSpecTypes =

    /// non-generic variable specification interface
    type IVarSpec =
        inherit System.IComparable
        abstract member Name : string 
        abstract member Shape: ShapeSpecT
        abstract member Type: System.Type
        abstract member TypeName: TypeNameT
        abstract member SubstSymSizes: SymSizeEnvT -> IVarSpec

    /// variable specification: has a name, type and shape specificaiton
    [<StructuredFormatDisplay("\"{Name}\" {Shape}")>]
    type VarSpecT<'T> = 
        {
            Name:      string; 
            Shape:     ShapeSpecT;
        }
        
        interface IVarSpec with
            member this.Name = this.Name
            member this.Shape = this.Shape
            member this.Type = typeof<'T>
            member this.TypeName = TypeName (typeof<'T>.AssemblyQualifiedName)
            member this.SubstSymSizes symSizes = 
                {this with Shape=SymSizeEnv.substShape symSizes this.Shape} :> IVarSpec

//        interface System.IComparable with
//            member this.CompareTo otherObj =
//                let this = this :> IVarSpec
//                match otherObj with
//                | :? IVarSpec as othr -> 
//                    compare 
//                        (this.Name, this.Shape, this.TypeName) 
//                        (othr.Name, othr.Shape, othr.TypeName)
//                | _ -> invalidArg "otherObj" "cannot compare values of different types"
//

    /// unified variable specification
    //[<CustomComparison>]
    type UVarSpecT = 
        {
            Name:      string; 
            Shape:     ShapeSpecT;
            TypeName:  TypeNameT;
        }

        interface IVarSpec with
            member this.Name = this.Name
            member this.Shape = this.Shape
            member this.Type = TypeName.getType this.TypeName
            member this.TypeName = this.TypeName
            member this.SubstSymSizes symSizes = 
                {this with Shape=SymSizeEnv.substShape symSizes this.Shape} :> IVarSpec

//        interface System.IComparable with
//            member this.CompareTo otherObj =
//                let this = this :> IVarSpec
//                match otherObj with
//                | :? IVarSpec as othr -> 
//                    compare 
//                        (this.Name, this.Shape, this.TypeName) 
//                        (othr.Name, othr.Shape, othr.TypeName)
//                | _ -> invalidArg "otherObj" "cannot compare values of different types"
//

module VarSpec =

    /// create variable specifation by name and shape
    let inline ofNameAndShape name shape =
        {VarSpecT.Name=name; Shape=shape;}
    
    /// create variable specifation by name and shape
    let inline ofNameShapeAndTypeName name shape typeName =
        {Name=name; Shape=shape; TypeName=typeName;}

    /// name of variable
    let name (vs: IVarSpec) = vs.Name

    /// shape of variable
    let shape (vs: IVarSpec) = vs.Shape

    /// substitutes the size symbol environment into the variable
    let substSymSizes symSizeEnv (vs: IVarSpec) = 
        vs.SubstSymSizes symSizeEnv

module UVarSpec =

    let ofVarSpec (vs: IVarSpec) =
        {Name=vs.Name; Shape=vs.Shape; TypeName=vs.TypeName}

    let toVarSpec (vs: IVarSpec) : VarSpecT<'T> =
        {Name=vs.Name; Shape=vs.Shape;}
