﻿namespace Models

open Basics
open ArrayNDNS
open SymTensor



/// A layer that calculates the loss between predictions and targets using a difference metric.
module LossLayer =

    /// Difference metrics.
    type Measures =
        /// mean-squared error 
        | MSE 
        /// binary cross entropy
        | BinaryCrossEntropy
        /// multi-class cross entropy
        | CrossEntropy

    /// Returns an expression for the loss given the loss measure `lm`, the predictions
    /// `pred` and the target values `target`.
    /// If the multi-class cross entropy loss measure is used then
    /// pred.[cls, smpl] must be the predicted probability that the sample
    /// belong to class cls and target.[cls, smpl] must be 1 if the sample
    /// actually belongs to class cls and 0 otherwise.
    let loss lm (pred: ExprT<'T>) (target: ExprT<'T>) =
        match lm with
        | MSE -> 
            (pred - target) ** Expr.two<'T>()
            |> Expr.mean
        | BinaryCrossEntropy ->
            -(target * log pred + (Expr.one<'T>() - target) * log (Expr.one<'T>() - pred))
            |> Expr.mean
        | CrossEntropy ->
            -target * log pred
            |> Expr.sumAxis 0
            |> Expr.mean


/// A layer of neurons (perceptrons).
module NeuralLayer = 

    /// Transfer (activation) functions.
    type TransferFuncs =
        /// tanh transfer function
        | Tanh
        /// soft-max transfer function
        | SoftMax
        /// no transfer function
        | Identity

    /// Neural layer hyper-parameters.
    type HyperPars = {
        /// number of inputs
        NInput:         SizeSpecT
        /// number of outputs
        NOutput:        SizeSpecT
        /// transfer (activation) function
        TransferFunc:   TransferFuncs
    }

    /// Neural layer parameters.
    type Pars<'T> = {
        /// expression for the weights
        Weights:        ExprT<'T> ref
        /// expression for the biases
        Bias:           ExprT<'T> ref
        /// hyper-parameters
        HyperPars:      HyperPars
    }

    let internal initWeights seed (shp: int list) : ArrayNDHostT<'T> = 
        let fanOut = shp.[0] |> float
        let fanIn = shp.[1] |> float
        let r = 4.0 * sqrt (6.0 / (fanIn + fanOut))
        let rng = System.Random seed
        
        rng.SeqDouble(-r, r)
        |> Seq.map conv<'T>
        |> ArrayNDHost.ofSeqWithShape shp
        
    let internal initBias seed (shp: int list) : ArrayNDHostT<'T> =
        Seq.initInfinite (fun _ -> conv<'T> 0)
        |> ArrayNDHost.ofSeqWithShape shp

    /// Creates the parameters for the neural-layer in the supplied
    /// model builder `mb` using the hyper-parameters `hp`.
    /// The weights are initialized using random numbers from a uniform
    /// distribution with support [-r, r] where
    /// r = 4 * sqrt (6 / (hp.NInput + hp.NOutput)).
    /// The biases are initialized to zero.
    let pars (mb: ModelBuilder<_>) hp = {
        Weights   = mb.Param ("Weights", [hp.NOutput; hp.NInput], initWeights)
        Bias      = mb.Param ("Bias",    [hp.NOutput],            initBias)
        HyperPars = hp
    }

    /// Returns an expression for the output (predictions) of the
    /// neural layer with parameters `pars` given the input `input`.
    /// If the soft-max transfer function is used, the normalization
    /// is performed over axis 0.
    let pred pars input =
        let activation = !pars.Weights .* input + !pars.Bias
        match pars.HyperPars.TransferFunc with
        | Tanh     -> tanh activation
        | SoftMax  -> exp activation / Expr.sumKeepingAxis 0 (exp activation)
        | Identity -> activation


/// A neural network (multi-layer perceptron) of multiple 
/// NeuralLayers and one LossLayer on top.
module MLP =

    /// MLP hyper-parameters.
    type HyperPars = {
        /// a list of the hyper-parameters of the neural layers
        Layers:         NeuralLayer.HyperPars list
        /// the loss measure
        LossMeasure:    LossLayer.Measures
    }

    /// MLP parameters.
    type Pars<'T> = {
        /// a list of the parameters of the neural layers
        Layers:         NeuralLayer.Pars<'T> list
        /// hyper-parameters
        HyperPars:      HyperPars
    }

    /// Creates the parameters for the neural network in the supplied
    /// model builder `mb` using the hyper-parameters `hp`.
    /// See `NeuralLayer.pars` for documentation about the initialization.
    let pars (mb: ModelBuilder<_>) (hp: HyperPars) = {
        Layers = hp.Layers 
                 |> List.mapi (fun idx nhp -> 
                    NeuralLayer.pars (mb.Module (sprintf "Layer%d" idx)) nhp)
        HyperPars = hp
    }

    /// Returns an expression for the output (predictions) of the
    /// neural network with parameters `pars` given the input `input`.
    let pred (pars: Pars<'T>) input =
        (input, pars.Layers)
        ||> List.fold (fun inp p -> NeuralLayer.pred p inp)

    /// Returns an expression for the loss of the
    /// neural network with parameters `pars` given the input `input` and
    /// target values `target`.       
    let loss pars input target =
        LossLayer.loss pars.HyperPars.LossMeasure (pred pars input) target




//module Autoencoder =
//
//    type Pars<'T> = {
//        InLayer:    NeuralLayer.Pars<'T>;
//        OutLayer:   NeuralLayer.Pars<'T>;
//    }
//
//    type HyperPars = {
//        NVisible:   SizeSpecT;
//        NLatent:    SizeSpecT;
//        Tied:       bool;
//    }
//
//    let pars (mc: ModelBuilder<_>) hp =
//        let p =
//            {InLayer   = NeuralLayer.pars (mc.Module "InLayer") hp.NVisible hp.NLatent;
//             OutLayer  = NeuralLayer.pars (mc.Module "OutLayer") hp.NLatent hp.NVisible;}
//        if hp.Tied then
//            {p with OutLayer = {p.OutLayer with Weights = p.InLayer.Weights.T}}
//        else p
//
//    let latent pars input =
//        NeuralLayer.pred pars.InLayer input
//
//    let recons pars input =
//        let hidden = latent pars input
//        NeuralLayer.pred pars.OutLayer hidden
//
//    let loss pars (input: ExprT<'T>) = 
//        let recons = recons pars input
//        let diff = (recons - input) ** Expr.two<'T>()
//        Expr.sum diff
//
