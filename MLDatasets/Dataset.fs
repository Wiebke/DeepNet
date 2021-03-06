﻿namespace Datasets

open System.Collections
open System.Collections.Generic
open Microsoft.FSharp.Reflection

open Basics
open ArrayNDNS


/// A dataset of a record type 'S containing ArrayNDT<_> data variables.
/// The first dimension of each record field is the sample.
/// All record fields must contain the same number of samples.
/// The default constructor expects a list of ArrayNDTs corresponding to the fields
/// in 'S. The first dimension must be the sample index.
/// To construct a Dataset<_> from a sequence of samples use the
/// Dataset<_>.FromSamples method.
[<StructuredFormatDisplay("Dataset ({NSamples} samples of {SampleType} in {Location} storage)")>]
type Dataset<'S> (fieldStorages: IArrayNDT list) =

    do if not (FSharpType.IsRecord typeof<'S>) then
        failwith "Dataset sample type must be a record containing ArrayNDTs"

    // make sure that all field storages are in C-order
    do for fs in fieldStorages do
        if not (ArrayND.isC fs) then
            failwith "all field storages in must be in C-order"

    // verify that all fields have equal number of samples
    let nSamples = fieldStorages.[0] |> ArrayND.shape |> List.head
    do
        fieldStorages
        |> List.iter (fun fs ->
            if ArrayND.shape fs |> List.head <> nSamples then
                invalidArg "fieldStorages" "unequal number of samples in fields")

    /// checks arguments for being in range
    let checkRange smpl =
        if not (smpl >= 0 && smpl < nSamples) then
            failwithf "sample index %d is out of range (have %d samples)" smpl nSamples

    /// Partitions this dataset using the given ratios.
    member this.Partition (ratios: float list) = 
        let ratioSum = List.sum ratios
        let partitionedFieldStorages = 
            fieldStorages
            |> List.map (fun fs ->
                let fsPart, _ =
                    (0, List.indexed ratios)
                    ||> List.mapFold (fun pos (idx, ratio) ->
                        let isLast = (idx = List.length ratios - 1)
                        let smpls =
                            if isLast then nSamples - pos
                            else int (ratio / ratioSum * (float nSamples))
                        fs.[pos .. pos+smpls-1, Fill], pos+smpls)    
                fsPart)
            |> List.transpose
        partitionedFieldStorages |> List.map Dataset<'S>

    /// Partitions this dataset using the given ratios.
    static member Partition (this: Dataset<'S>, ratios) = this.Partition ratios

    /// Returns a record of type 'S containing the sample with the given index.
    member this.Item 
        with get (smpl: int) =
            checkRange smpl
            let smplData =
                [| for fs in fieldStorages -> fs.[smpl, Fill] |> box |]
            FSharpValue.MakeRecord (typeof<'S>, smplData) :?> 'S

    /// Returns a record of type 'S containing a slice of samples.
    member this.GetSlice (start: int option, stop: int option) =
        match start with | Some smpl -> checkRange smpl | None -> ()
        match stop  with | Some smpl -> checkRange smpl | None -> ()  
        let sliceData =
            [| for fs in fieldStorages -> fs.[[Rng (start, stop); RngAllFill]] |> box |]
        FSharpValue.MakeRecord (typeof<'S>, sliceData) :?> 'S            
                            
    /// Returns a record of type 'S containing all samples.
    member this.All = 
        let allData =
            [| for fs in fieldStorages -> fs |> box |]
        FSharpValue.MakeRecord (typeof<'S>, allData) :?> 'S            

    /// number of samples
    member this.NSamples = nSamples

    /// data type of samples
    member this.SampleType = typeof<'S>

    /// storage location
    member this.Location = fieldStorages.[0].Location

    /// Generates a function that returns a sequence of batches with the given size of this dataset.
    /// If the number of samples in this dataset is not a multiple of the batch size,
    /// the last batch will still have the specified size but is padded with zeros.
    member this.PaddedBatches batchSize = 
        let lastBatchElems = nSamples % batchSize
        let lastBatchStart = nSamples - lastBatchElems

        // create padded last batch, if necessary
        let lastBatch =
            if lastBatchElems = 0 then None
            else                   
                fieldStorages
                |> List.map (fun fsAll ->
                    let shpAll = ArrayND.shape fsAll
                    let shpBatch = shpAll |> List.set 0 batchSize                    
                    let fsBatch = fsAll |> ArrayND.newCOfSameType shpBatch 
                    fsBatch.[0 .. lastBatchElems-1, Fill] <- fsAll.[lastBatchStart .. nSamples-1, Fill]
                    fsBatch)
                |> Some

        fun () ->                    
            seq {
                // all batches except last batch if padding was necessary
                for start in 0 .. batchSize .. lastBatchStart-1 do
                    let stop = start + batchSize - 1
                    yield this.[start .. stop]  
                    
                // padded last batch if necessary
                match lastBatch with
                | Some lastBatch ->
                    let data = [|for fs in lastBatch -> fs |> box|]
                    yield FSharpValue.MakeRecord (typeof<'S>, data) :?> 'S     
                | None -> ()        
            }           

    /// Returns a sequence of batches with the given size of this dataset.
    /// If the number of samples in this dataset is not a multiple of the batch size,
    /// the last batch will be smaller.
    member this.Batches batchSize = 
        let lastBatchElems = nSamples % batchSize
        let lastBatchStart = nSamples - lastBatchElems

        seq {
            // all batches except last batch 
            for start in 0 .. batchSize .. lastBatchStart-1 do
                let stop = start + batchSize - 1
                yield this.[start .. stop]  
                    
            // last batch 
            if lastBatchStart < nSamples then
                yield this.[lastBatchStart ..]
        }          

    /// template batch
    member this.TmplBatch batchSize = 
        this.PaddedBatches batchSize () |> Seq.head

    /// maps the field storages using the given function creating a new dataset
    member this.MapFieldStorage (f: IArrayNDT -> #IArrayNDT) =
        fieldStorages
        |> List.map (f >> (fun fs -> fs :> IArrayNDT))
        |> Dataset<'S>

    /// copies this dataset to a CUDA GPU
    member this.ToCuda () =
        this.MapFieldStorage (fun fs ->
            ArrayNDCuda.toDevUntyped (fs :?> IArrayNDHostT))

    /// copies this dataset to the host
    member this.ToHost () =
        this.MapFieldStorage (fun fs ->
            ArrayNDCuda.toHostUntyped (fs :?> IArrayNDCudaT))

    /// copies this dataset to a CUDA GPU
    static member ToCuda (this: Dataset<'S>) = this.ToCuda ()

    /// copies this dataset to the host
    static member ToHost (this: Dataset<'S>) = this.ToHost ()

    /// saves this dataset into a HDF5 file
    member this.Save filename =
        let fldInfos = FSharpType.GetRecordFields (typeof<'S>)
        use hdf = HDF5.OpenWrite filename
        for fldInfo, fs in Seq.zip fldInfos fieldStorages do
            match fs with
            | :? IArrayNDHostT as fs -> ArrayNDHDF.writeUntyped hdf fldInfo.Name fs
            | _ -> failwith "can only save a dataset stored on the host"

    /// loads a dataset from a HDF5 file
    static member Load<'S> filename =
        if not (FSharpType.IsRecord typeof<'S>) then
            failwith "Dataset sample type must be a record containing ArrayNDHostTs"
        use hdf = HDF5.OpenRead filename
        FSharpType.GetRecordFields typeof<'S>
        |> Seq.map (fun fldInfo ->
            if not (typeof<IArrayNDT>.IsAssignableFrom fldInfo.PropertyType) then 
                failwith "Dataset sample type must be a record containing ArrayNDHostTs"
            let dataType = fldInfo.PropertyType.GenericTypeArguments.[0]
            ArrayNDHDF.readUntyped hdf fldInfo.Name dataType :> IArrayNDT)
        |> Seq.toList
        |> Dataset<'S>

    // enumerator interfaces
    interface IEnumerable<'S> with
        member this.GetEnumerator() =
            (seq { for idx in 0 .. nSamples - 1 -> this.[idx] }).GetEnumerator()
    interface IEnumerable with
        member this.GetEnumerator() =
            (this :> IEnumerable<'S>).GetEnumerator() :> IEnumerator


    /// Constructs a dataset from a sequence of samples of record type 'S.
    /// Each field in 'S must be of type ArrayNDT<_> and the dimensionality of each field
    /// must be constant over all samples.
    /// If the shape of a field varies over the samples it is padded (with zeros) to the largest 
    /// shape in the sample sequence.
    /// The given sequence is enumerated only one time and the data is copied once.
    static member FromSamples (samples: 'S seq) =          
        let samples = Seq.cache samples 
        if Seq.isEmpty samples then
            invalidArg "samples" "need at least one sample to create a Dataset"

        // ary.[smpl,field] : IArrayNDT[,]
        let nFields = Array.length (FSharpValue.GetRecordFields (Seq.head samples))
        let nSamples = Seq.length samples
        let ary = Array2D.zeroCreate nSamples nFields
        for smpl, value in Seq.indexed samples do
            ary.[smpl, *] <-
                FSharpValue.GetRecordFields value
                |> Array.map (fun v -> v :?> IArrayNDT)

        // find largest shape of each field over all samples
        let maxShape (fieldSmpls: IArrayNDT seq) =
            let mutable maxShape = Seq.head fieldSmpls |> ArrayND.shape
            for smpl in fieldSmpls do
                let smplShape = ArrayND.shape smpl
                if List.length smplShape <> List.length maxShape then
                    failwith "dimensionality of a field must be equal over all samples"
                maxShape <- (maxShape, smplShape) ||> List.map2 max
            maxShape

        // build data storage
        let fieldStorage (fieldSmpls: IArrayNDT seq) =
            let maxSmplShp = maxShape fieldSmpls
            let storShp = nSamples :: maxSmplShp
            let fieldTyp = (Seq.head fieldSmpls).DataType
            let stor = ArrayNDHost.newCOfType fieldTyp storShp 
            for smpl, smplVal in Seq.indexed fieldSmpls do
                stor.[smpl, Fill] <- smplVal
            stor :> IArrayNDT            

        let fieldStorages = 
            seq { for fld=0 to nFields-1 do yield async { return fieldStorage ary.[*, fld] } }
            |> Async.Parallel |> Async.RunSynchronously
            |> Array.toList
        Dataset<'S> fieldStorages

 

/// A training/validation/test partitioning of a dataset.
[<StructuredFormatDisplay("{Pretty}")>]
type TrnValTst<'S> = { 
    /// training partition
    Trn:    Dataset<'S>
    /// validation partition
    Val:    Dataset<'S>
    /// test partition
    Tst:    Dataset<'S> 
} with 
    /// Creates the partitioning from the specified dataset.
    static member Of (dataset: Dataset<'S>, ?trnRatio: float, ?valRatio: float, ?tstRatio: float) =
        let trnRatio = defaultArg trnRatio 0.80
        let valRatio = defaultArg valRatio 0.10
        let tstRatio = defaultArg tstRatio 0.10
        match dataset.Partition [trnRatio; valRatio; tstRatio] with
        | [trn; vali; tst] -> {Trn=trn; Val=vali; Tst=tst}
        | _ -> failwith "impossible"

    member internal this.Pretty = 
        sprintf "Dataset (%d training, %d validation, %d test %As)"
            this.Trn.NSamples this.Val.NSamples this.Tst.NSamples this.Trn.SampleType
            
    /// Copies this dataset to a CUDA GPU.
    member this.ToCuda () = {
        Trn = this.Trn.ToCuda ()
        Val = this.Val.ToCuda ()
        Tst = this.Tst.ToCuda ()
    }

    /// Copies this dataset to the host.
    member this.ToHost () = {
        Trn = this.Trn.ToHost ()
        Val = this.Val.ToHost ()
        Tst = this.Tst.ToHost ()
    }

    /// Copies the given dataset to a CUDA GPU.
    static member ToCuda (this: TrnValTst<'S>) = 
        this.ToCuda ()

    /// Copies the given dataset to the host.
    static member ToHost (this: TrnValTst<'S>) =
        this.ToHost ()

    /// Saves this dataset to disk.
    /// The given filename is append with '-Trn.h5', '-Val.h5' and '-Tst.h5'
    /// for the training, validation and test set respectively.
    member this.Save filename =
        this.Trn.Save (filename + "-Trn.h5")
        this.Val.Save (filename + "-Val.h5")
        this.Tst.Save (filename + "-Tst.h5")

    /// Loads a dataset from disk.
    /// The given filename is append with '-Trn.h5', '-Val.h5' and '-Tst.h5'
    /// for the training, validation and test set respectively.
    static member Load filename : TrnValTst<'S> = {
        Trn = Dataset.Load (filename + "-Trn.h5")
        Val = Dataset.Load (filename + "-Val.h5")
        Tst = Dataset.Load (filename + "-Tst.h5")
    }
                

