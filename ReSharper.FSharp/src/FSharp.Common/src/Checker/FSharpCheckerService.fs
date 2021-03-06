namespace rec JetBrains.ReSharper.Plugins.FSharp.Common.Checker

open System
open System.Runtime.InteropServices
open JetBrains
open JetBrains.Annotations
open JetBrains.Application
open JetBrains.Application.Settings
open JetBrains.DataFlow
open JetBrains.ReSharper.Feature.Services
open JetBrains.ReSharper.Psi
open JetBrains.ReSharper.Plugins.FSharp
open JetBrains.ReSharper.Plugins.FSharp.Common.Checker.Settings
open JetBrains.ReSharper.Plugins.FSharp.Common.Util
open JetBrains.ReSharper.Psi.Modules
open JetBrains.Util
open Microsoft.FSharp.Compiler.SourceCodeServices

[<ShellComponent; AllowNullLiteral>]
type FSharpCheckerService
        (lifetime, logger: ILogger, onSolutionCloseNotifier: OnSolutionCloseNotifier, settingsStore: ISettingsStore) =

    let checker =
        Environment.SetEnvironmentVariable("FCS_CheckFileInProjectCacheSize", "20")

        let enableBgCheck =
            settingsStore
                .BindToContextLive(lifetime, ContextRange.ApplicationWide)
                .GetValueProperty(lifetime, fun (key: FSharpOptions) -> key.BackgroundTypeCheck)

        lazy
            let checker =
                FSharpChecker.Create(projectCacheSize = 200,
                                     keepAllBackgroundResolutions = false,
                                     ImplicitlyStartBackgroundWork = enableBgCheck.Value)

            enableBgCheck.Change.Advise_NoAcknowledgement(lifetime, fun (ArgValue enabled) ->
                checker.ImplicitlyStartBackgroundWork <- enabled)

            checker

    do
        onSolutionCloseNotifier.SolutionIsAboutToClose.Advise(lifetime, fun _ ->
            if checker.IsValueCreated then
                checker.Value.InvalidateAll())

    member val OptionsProvider = Unchecked.defaultof<IFSharpProjectOptionsProvider> with get, set
    member x.Checker = checker.Value

    member x.ParseFile([<NotNull>] file: IPsiSourceFile) =
        let parsingOptions = x.OptionsProvider.GetParsingOptions(file)
        let filePath = file.GetLocation().FullPath
        let parsingOptions =
            if not (Array.isEmpty parsingOptions.SourceFiles) then parsingOptions
            else
                let project = file.GetProject().GetLocation().FullPath
                logger.Warn("Loading from caches, don't have source files for {0} yet.", project)
                { parsingOptions with SourceFiles = [| filePath |] }
        let source = file.Document.GetText()
        try
            let parseResults = x.Checker.ParseFile(filePath, source, parsingOptions).RunAsTask() 
            Some parseResults
        with
        | :? OperationCanceledException -> reraise()
        | exn ->
            Util.Logging.Logger.LogException(exn)
            logger.Warn(sprintf "Parse file error, parsing options: %A" parsingOptions)
            None

    member x.HasPairFile([<NotNull>] file: IPsiSourceFile) =
        x.OptionsProvider.HasPairFile(file)

    member x.GetDefines(sourceFile: IPsiSourceFile) =
        x.OptionsProvider.GetParsingOptions(sourceFile).ConditionalCompilationDefines

    member x.ParseAndCheckFile([<NotNull>] file: IPsiSourceFile, opName,
                               [<Optional; DefaultParameterValue(false)>] allowStaleResults) =
        match x.OptionsProvider.GetProjectOptions(file) with
        | None -> None
        | Some options ->

        let path = file.GetLocation().FullPath
        let source = file.Document.GetText()
        logger.Trace("ParseAndCheckFile: start {0}, {1}", path, opName)

        // todo: don't cancel the computation when file didn't change
        match x.Checker.ParseAndCheckDocument(path, source, options, allowStaleResults, opName).RunAsTask() with
        | Some (parseResults, checkResults) when parseResults.ParseTree.IsSome ->
            logger.Trace("ParseAndCheckFile: finish {0}, {1}", path, opName)
            Some { ParseResults = parseResults; CheckResults = checkResults }

        | _ ->
            logger.Trace("ParseAndCheckFile: fail {0}, {1}", path, opName)
            None

    member x.TryGetStaleCheckResults([<NotNull>] file: IPsiSourceFile, opName) =
        match x.OptionsProvider.GetProjectOptions(file) with
        | None -> None
        | Some options ->

        let path = file.GetLocation().FullPath
        logger.Trace("TryGetStaleCheckResults: start {0}, {1}", path, opName)

        match x.Checker.TryGetRecentCheckResultsForFile(path, options) with
        | Some (_, checkResults, _) ->
            logger.Trace("TryGetStaleCheckResults: finish {0}, {1}", path, opName)
            Some checkResults

        | _ ->
            logger.Trace("TryGetStaleCheckResults: fail {0}, {1}", path, opName)
            None


type FSharpParseAndCheckResults = 
    { ParseResults: FSharpParseFileResults
      CheckResults: FSharpCheckFileResults }


type IFSharpProjectOptionsProvider =
    abstract GetProjectOptions: IPsiSourceFile -> FSharpProjectOptions option
    abstract GetParsingOptions: IPsiSourceFile -> FSharpParsingOptions
    abstract GetFileIndex: IPsiSourceFile -> int
    abstract HasPairFile: IPsiSourceFile -> bool
    abstract ModuleInvalidated: ISignal<IPsiModule>
