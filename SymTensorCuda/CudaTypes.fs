﻿namespace SymTensor.Compiler.Cuda

open System
open System.Runtime.InteropServices
open ManagedCuda

open Util
open ArrayNDNS
open SymTensor
open SymTensor.Compiler


#nowarn "9"


[<AutoOpen>]
module Types =

    /// device memory pointer
    type DevMemPtrT = {
        /// base memory
        Base: MemManikinT;
        /// offset in elements
        Offset: int}

    /// pre-allocated host memory 
    type HostExternalMemT = {Name: string}
    /// host memory pointer
    type HostMemPtrT = {Base: HostExternalMemT;
                        Offset: int}


    /// variable storage location
    type VarStorLocT =
        /// variable stored on device
        | LocDev
        /// variable stored on host
        | LocHost

    /// additional environment informations for CUDA
    type CudaCompileEnvT = {VarStorLoc: Map<IVarSpec, VarStorLocT>}

    /// function domain (kernel only or host code that may call kernels)
    type FuncDomainT =
        | KernelFunc
        | CPPFunc

    /// template instantiation specification
    type TmplInstT = {FuncName: string; Domain: FuncDomainT; 
                      TmplArgs: string list; RetType: string; ArgTypes: string list;}


    /// Actual CUDA internal memory allocations and external device and host references
    type CudaExecEnvT = 
        {InternalMem: Dictionary<MemAllocManikinT, CudaDeviceVariable<byte>>;
         ExternalVar: Map<IVarSpec, ArrayNDCuda.IArrayNDCudaT>;
         HostVar:     Map<IVarSpec, ArrayNDHost.IArrayNDHostT>}
    
    /// CUDA device memory range
    type DevMemRngT = 
        {DeviceMem: CudaDeviceVariable<byte>;
         OffsetInBytes: int;
         LengthInBytes: int;}

    /// CUDA host memory range
    type HostMemRngT = 
        {HostMem: CudaRegisteredHostMemory<byte>;
         OffsetInBytes: int;
         LengthInBytes: int;}


    /// BLAS transpose operation
    type BlasTransposeOpT =
        | BlasId
        | BlasTranspose

        member this.CudaBlasOperation =
            match this with
            | BlasId -> CudaBlas.Operation.NonTranspose
            | BlasTranspose -> CudaBlas.Operation.Transpose

    /// specifies the name of the C++ function
    type CPPFuncNameAttribute (cppFuncName: string) =
        inherit System.Attribute()     
        member this.CPPFuncName = cppFuncName

    /// a CUDA compute stream
    type StreamT = int

    /// a CUDA event that can be used for synchronization
    type EventT = {EventObjectId: int; CorrelationId: int; EmittingExecUnitId: int}



module CudaExecEnv = 

    /// gets device memory for an internal allocation or external reference
    let getDevMemForManikin (env: CudaExecEnvT) (manikin: ArrayNDManikinT) =
        match manikin.Storage with
        | MemAlloc im -> env.InternalMem.[im]
        | MemExternal vs ->
            let ev = env.ExternalVar.[vs]
            if (ArrayND.shape ev) = (ArrayND.shape manikin) && 
                    (ArrayND.stride ev) = (ArrayND.stride manikin) && 
                    (ArrayND.offset ev) = (ArrayND.offset manikin) then
                (ev :?> ArrayNDCuda.IDeviceStorage).ByteData
            else
                failwithf "external variable is of form %A but form %A was expected" ev manikin

    /// gets host memory for an external reference
    let getHostRegMemForManikin (env: CudaExecEnvT) (manikin: ArrayNDManikinT) =
        match manikin.Storage with
        | MemExternal vs ->
            let hv = env.HostVar.[vs]
            if (ArrayND.shape hv) = (ArrayND.shape manikin) && 
                    (ArrayND.stride hv) = (ArrayND.stride manikin) && 
                    (ArrayND.offset hv) = (ArrayND.offset manikin) then
                ArrayNDHostReg.getCudaRegisteredMemory hv
            else
                failwithf "host variable is of form %A but form %A was expected" hv manikin
        | _ -> failwithf "host variable must be of type ExternalMem"


[<AutoOpen>]
module ArgTemplates =

    /// CUDA C++ argument template
    type ICudaArgTmpl =
        abstract member CPPTypeName : string
        abstract member GetArg : CudaExecEnvT -> obj 

    /// CUDA C++ operation functor description
    type ICudaOp =
        abstract member IsIndexed : bool  

    /// CUDA device memory range template
    type IDevMemRngTmpl =
        abstract member GetRng : CudaExecEnvT -> DevMemRngT

    /// CUDA host memory range template
    type IHostMemRngTmpl =
        abstract member GetRng : CudaExecEnvT -> HostMemRngT

    /// ArrayND argument template
    type ArrayNDArgTmpl (manikin: ArrayNDManikinT) = 
        interface ICudaArgTmpl with
            member this.CPPTypeName = manikin.CPPType
            member this.GetArg env =
                // C++ struct just contains the pointer to data memory
                (CudaExecEnv.getDevMemForManikin env manikin).DevicePointer :> obj

    /// device memory range over the elements of a contiguous ArrayND
    type ArrayNDDevMemRngTmpl (manikin: ArrayNDManikinT) =
        interface IDevMemRngTmpl with
            member this.GetRng env =
                {DeviceMem = CudaExecEnv.getDevMemForManikin env manikin;
                 OffsetInBytes = ArrayNDManikin.offsetInBytes manikin;
                 LengthInBytes = ArrayNDManikin.sizeInBytes manikin;}
    
    /// registered host memory range over the elements of a contiguous ArrayND    
    type ArrayNDHostRegMemRngTmpl (manikin: ArrayNDManikinT) =
        interface IHostMemRngTmpl with
            member this.GetRng env =
                {HostMem = CudaExecEnv.getHostRegMemForManikin env manikin;
                 OffsetInBytes = ArrayNDManikin.offsetInBytes manikin;
                 LengthInBytes = ArrayNDManikin.sizeInBytes manikin;}      

    /// BLAS view of ArrayND. The ArrayND is implicitly transposed and exposed as a "float *"
    type BlasTransposedMatrixTmpl (manikin: ArrayNDManikinT) =
        // All CUBLAS calls use Fortran matrices. This means:
        // - one-based indexing
        // - column major
        // For ArrayND this translates to:
        // CUBLAS #columns    = Shape.[0]
        // CUBLAS #rows       = Shape.[1]
        // CUBLAS leading dim = Stride.[0] >= 1 (no broadcasting)
        // Stride.[1] must be 1.

        do
            if not ((manikin |> ArrayNDManikin.typeName |> TypeName.getType).Equals(typeof<single>)) then
                failwith "CUBLAS currently requires single values"
            match ArrayND.stride manikin with
            | [0; _] -> failwithf "ArrayND for use with BLAS cannot be broadcasted in first dimension"
            | [_; n] when n <> 1 -> failwithf "ArrayND for use with BLAS must be continguous in last dimension but has stride %d" n
            | [_; _] -> ()
            | _ -> failwith "ArrayND for use with BLAS must be 2-dimensional"         

        member this.GetLeadingDimension env =
            (ArrayND.stride manikin).[0] 

        member this.GetColumns env =
            (ArrayND.shape manikin).[0]

        member this.GetRows env =
            (ArrayND.shape manikin).[1]

        member this.GetColumnsForOp env op =
            match op with 
            | CudaBlas.Operation.NonTranspose -> this.GetColumns env
            | CudaBlas.Operation.Transpose 
            | CudaBlas.Operation.ConjugateTranspose -> this.GetRows env
            | _ -> failwithf "unknown CudaBlas.Operation %A" op

        member this.GetRowsForOp env op =
            match op with 
            | CudaBlas.Operation.NonTranspose -> this.GetRows env
            | CudaBlas.Operation.Transpose 
            | CudaBlas.Operation.ConjugateTranspose -> this.GetColumns env
            | _ -> failwithf "unknown CudaBlas.Operation %A" op

        interface ICudaArgTmpl with
            member this.CPPTypeName = "float"
            member this.GetArg env = 
                let devVar = CudaExecEnv.getDevMemForManikin env manikin
                // need to adjust by offset
                let offsetBytes = ArrayNDManikin.offsetInBytes manikin
                new CudaDeviceVariable<single>(devVar.DevicePointer + BasicTypes.SizeT(offsetBytes), 
                                               devVar.SizeInBytes - offsetBytes) :> obj


    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    /// const value elementwise operation C++ structure
    type ConstEOpArg<'T when 'T: struct> =
        val Value: 'T
        new (value: 'T) = {Value = value}

    type ConstEOpArgTmpl<'T> (value: 'T) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = "ConstEOp_t"
            member this.GetArg env = 
                match box value with
                | :? single as n -> ConstEOpArg(n) :> obj
                | :? double as n -> ConstEOpArg(n) :> obj
                | :? int as n -> ConstEOpArg(n) :> obj
                | :? byte as n -> ConstEOpArg(n) :> obj
                | _ -> failwithf "unsupported type %A" (value.GetType())
        interface ICudaOp with
            member this.IsIndexed = false

    [<Struct>]
    [<type: StructLayout(LayoutKind.Sequential, Pack=4)>]
    type NoArgEOpArg = struct end
    
    type NoArgEOpArgTmpl (cppTypeName: string, indexed: bool) =
        interface ICudaArgTmpl with
            member this.CPPTypeName = cppTypeName
            member this.GetArg env = NoArgEOpArg() :> obj
        interface ICudaOp with
            member this.IsIndexed = indexed


[<AutoOpen>]
module NativeFunctionDelegates =

    [<CPPFuncName("sum")>]
    type CPPSum = delegate of BasicTypes.CUdeviceptr * BasicTypes.CUdeviceptr -> unit
    //type CPPSum = delegate of NDArrayDev * NDArrayDev -> unit

    [<CPPFuncName("sumLastAxis")>]
    type CPPSumLastAxis = delegate of BasicTypes.CUdeviceptr * BasicTypes.CUdeviceptr -> unit
    //type CPPSumLastAxis = delegate of NDArrayDev * NDArrayDev -> unit
