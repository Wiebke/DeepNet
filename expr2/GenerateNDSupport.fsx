﻿open System.Text
open System.IO

let maxDims = 5

let combineWith sep items =    
    let rec combine items = 
        match items with
        | [item] -> item
        | item::rest -> item + sep + combine rest
        | [] -> ""
    items |> Seq.toList |> combine

let combineWithButIfEmpty empty sep items =
    if Seq.isEmpty items then empty
    else combineWith sep items

let sw = new StreamWriter("NDSupport.cuh")
let prn = sprintf
let cw = combineWith
let cwe = combineWithButIfEmpty
let (|>>) seq mapFun = Seq.map mapFun seq

let wrt frmt = fprintfn sw frmt
 


for dims = 0 to maxDims do
    let ad = {0 .. dims-1}

    wrt "template <%s>" 
        (ad |>> prn "size_t shape%d" |> cw ", ")
    wrt "class Shape%dD {" dims
    wrt "public:"
    wrt "  	_dev static size_t shape(const size_t dim) {"
    wrt "      switch (dim) {"
    for d in ad do
        wrt "        case %d: return shape%d;" d d
    wrt "        default: return 0;"
    wrt "      }"
    wrt "};"
    wrt ""

    wrt "template <%s>" 
        (ad |>> prn "size_t stride%d" |> cw ", ")
    wrt "class Stride%dD {" dims
    wrt "public:"
    wrt "  	_dev static size_t stride(const size_t dim) {"
    wrt "      switch (dim) {"
    for d in ad do
        wrt "        case %d: return stride%d;" d d
    wrt "        default: return 0;"
    wrt "      }"
    wrt "    }"
    wrt ""
    wrt "  	_dev static size_t offset(%s) {"
        (ad |>> prn "const size_t pos%d" |> cw ", ")
    wrt "      return %s;"
        (ad |>> (fun i -> prn "stride%d * pos%d" i i) |> cwe "0" " + ")
    wrt "    }"
    wrt "};"
    wrt ""

    wrt "template <typename TShape, typename TStride>"
    wrt "class NDArray%dD {" dims
    wrt "public:"
    wrt "  typedef TShape Shape;"
    wrt "  typedef TStride Stride;"
    wrt "  _dev static size_t shape(const size_t dim) { return Shape::shape(dim); }"
    wrt "  _dev static size_t stride(const size_t dim) { return Stride::stride(dim); }"
    wrt "  _dev static size_t size() {"
    wrt "    return %s;"
        (ad |>> prn "shape(%d)" |> cwe "1" " * ")
    wrt "  }"
    wrt "  _dev float *data() { return reinterpret_cast<float *>(this); }"
    wrt "  _dev const float *data() const { return reinterpret_cast<const float *>(this); }"
    wrt "  _dev float &element(%s) {"
        (ad |>> prn "size_t pos%d" |> cw ", ")
    wrt "    return data()[Stride::offset(%s)];"
        (ad |>> prn "pos%d" |> cw ", ")
    wrt "  }"
    wrt "  _dev const float &element(%s) const {"
        (ad |>> prn "size_t pos%d" |> cw ", ")
    wrt "    return data()[Stride::offset(%s)];"
        (ad |>> prn "pos%d" |> cw ", ")
    wrt "  }"
    wrt "};"
    wrt ""

    let calculateElementwisePos () =
        if dims > 3 then
            wrt ""
            wrt "  size_t posRest = threadIdx.z + blockIdx.z * blockDim.z;"
            wrt "  const size_t incr2 = 1;"
            for d = 3 to dims - 1 do
                wrt "  const size_t incr%d = incr%d * TTarget::shape(%d);" d (d-1) (d-1)
            for d = dims - 1 downto 2 do
                wrt "  const size_t pos%d = posRest / incr%d;" d d
                wrt "  posRest -= pos%d * incr%d;" d d
        if dims = 3 then
            wrt "  const size_t pos2 = threadIdx.z + blockIdx.z * blockDim.z;"
        if dims >= 2 then
            wrt "  const size_t pos1 = threadIdx.y + blockIdx.y * blockDim.y;"
        if dims >= 1 then
            wrt "  const size_t pos0 = threadIdx.x + blockIdx.x * blockDim.x;"
            wrt "  if (!(%s)) return;"
                (ad |>> (fun i -> prn "(pos%d < trgt->shape(%d))" i i) |> cw " && ")
        wrt ""

    wrt "template <typename TUnaryElementwiseOp, typename TTarget, typename TA>"
    wrt "__global__ void elementwiseUnary%dD(TTarget *trgt, const TA *a) {" dims
    wrt "  TUnaryElementwiseOp op;"
    calculateElementwisePos()
    let poses = ad |>> prn "pos%d" |> cw ", "
    wrt "  trgt->element(%s) = op(a->element(%s));" poses poses
    wrt "}"
    wrt ""

    wrt "template <typename TBinaryElementwiseOp, typename TTarget, typename TA, typename TB>"
    wrt "__global__ void elementwiseUnary%dD(TTarget *trgt, const TA *a, const TB *b) {" dims
    wrt "  TBinaryElementwiseOp op;"
    calculateElementwisePos()
    let poses = ad |>> prn "pos%d" |> cw ", "
    wrt "  trgt->element(%s) = op(a->element(%s), b->element(%s));" poses poses poses
    wrt "}"
    wrt ""

    ()


sw.Dispose()


