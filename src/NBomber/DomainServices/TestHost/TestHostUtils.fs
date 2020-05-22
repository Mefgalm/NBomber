namespace NBomber.DomainServices.TestHost

open System
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Reactive
open FsToolkit.ErrorHandling

open NBomber
open NBomber.Contracts
open NBomber.Extensions
open NBomber.Domain
open NBomber.Domain.DomainTypes
open NBomber.Domain.Concurrency.Scheduler.ScenarioScheduler
open NBomber.Domain.ConnectionPool
open NBomber.Infra.Dependency
open NBomber.DomainServices.NBomberContext

module internal TestHostReporting =

    let saveRealtimeStats (sinks: IReportingSink list) (nodeStats: NodeStats list) =
        sinks
        |> List.map(fun x -> nodeStats |> List.toArray |> x.SaveRealtimeStats)
        |> Task.WhenAll

    let saveFinalStats (dep: IGlobalDependency) (stats: NodeStats list) =
        for sink in dep.ReportingSinks do
            try
                sink.SaveFinalStats(stats |> Seq.toArray).Wait()
            with
            | ex -> dep.Logger.Error(ex, "ReportingSink '{SinkName}' failed", sink.SinkName)

    let startReportingTimer (dep: IGlobalDependency,
                             sendStatsInterval: TimeSpan,
                             getData: unit -> (NodeOperationType * NodeStats)) =

            let timer = new System.Timers.Timer(sendStatsInterval.TotalMilliseconds)
            timer.Elapsed.Add(fun _ ->

                let (operation,nodeStats) = getData()

                match operation with
                | NodeOperationType.Bombing ->
                    if not (List.isEmpty dep.ReportingSinks) then
                        nodeStats
                        |> List.singleton
                        |> saveRealtimeStats dep.ReportingSinks
                        |> ignore

                | _ -> ()
            )
            timer.Start()
            timer

    let printReportingSinks (dep: IGlobalDependency) =
        dep.ReportingSinks
        |> List.map(fun x -> x.SinkName)
        |> fun sinks -> dep.Logger.Information("reporting sinks loaded: {0}", String.concatWithCommaAndQuotes sinks)

    let startReportingSinks (dep: IGlobalDependency) (testInfo: TestInfo) =
        for sink in dep.ReportingSinks do
            try
                sink.StartTest(testInfo) |> ignore
            with
            | ex -> dep.Logger.Error(ex, "ReportingSink '{SinkName}' failed", sink.SinkName)

    let stopReportingSinks (dep: IGlobalDependency) =
        for sink in dep.ReportingSinks do
            try
                sink.StopTest().Wait()
            with
            | ex -> dep.Logger.Error(ex, "ReportingSink '{SinkName}' failed", sink.SinkName)

module internal TestHostPlugins =

    let printPlugins (dep: IGlobalDependency) =
        dep.Plugins
        |> List.map(fun x -> x.PluginName)
        |> fun plugins -> dep.Logger.Information("plugins loaded: {0}", String.concatWithCommaAndQuotes plugins)

    let startPlugins (dep: IGlobalDependency) (testInfo: TestInfo) =
        for plugin in dep.Plugins do
            try
                plugin.StartTest(testInfo).Wait()
            with
            | ex -> dep.Logger.Error(ex, "Plugin '{PluginName}' failed", plugin.PluginName)

    let stopPlugins (dep: IGlobalDependency) =
        for plugin in dep.Plugins do
            try
                plugin.StopTest().Wait()
                plugin.Dispose()
            with
            | ex -> dep.Logger.Error(ex, "Plugin '{PluginName}' failed", plugin.PluginName)

module internal TestHostConfig =

    let printNBomberConfig (dep: IGlobalDependency) =
        dep.Logger.Verbose("NBomberConfig: {NBomberConfig}", sprintf "%A" dep.NBomberConfig)

module internal TestHostConsole =

    let printTargetScenarios (dep: IGlobalDependency, targetScns: Scenario list) =
        targetScns
        |> List.map(fun x -> x.ScenarioName)
        |> fun targets -> dep.Logger.Information("target scenarios: {0}", String.concatWithCommaAndQuotes targets)

    let displayBombingProgress (dep: IGlobalDependency, scnSchedulers: ScenarioScheduler list, isWarmUp: bool) =

        let calcTickCount (scn: Scenario) =
            if isWarmUp then int(scn.WarmUpDuration.TotalMilliseconds / Constants.SchedulerNotificationTickInterval.TotalMilliseconds)
            else int(scn.PlanedDuration.TotalMilliseconds / Constants.SchedulerNotificationTickInterval.TotalMilliseconds)

        let getLongestDuration (schedulers: ScenarioScheduler list) =
            if isWarmUp then schedulers |> List.map(fun x -> x.Scenario.WarmUpDuration) |> List.max
            else schedulers |> List.map(fun x -> x.Scenario.PlanedDuration) |> List.max

        let displayProgressForConcurrentScenarios (schedulers: ScenarioScheduler list) =
            let mainPb = schedulers |> getLongestDuration |> dep.ProgressBarEnv.CreateAutoProgressBar

            schedulers
            |> List.map(fun scheduler ->
                let tickCount = calcTickCount(scheduler.Scenario)
                let pb = mainPb.Spawn(tickCount, "")

                scheduler.EventStream
                |> Observable.subscribeWithCompletion
                    (fun x -> let simulationName = LoadTimeLine.getSimulationName(x.CurrentSimulation)
                              let msg = String.Format("scenario: '{0}', simulation: '{1}', keep concurrent: '{2}', inject per sec: '{3}'",
                                                      scheduler.Scenario.ScenarioName, simulationName, x.ConstantActorCount, x.OneTimeActorCount)
                              pb.Tick(msg))
                    (fun () -> pb.Dispose())
            )
            |> List.append [mainPb :> IDisposable]

        let displayProgressForOneScenario (scheduler: ScenarioScheduler) =
            let pb = scheduler.Scenario |> calcTickCount |> dep.ProgressBarEnv.CreateManualProgressBar
            scheduler.EventStream
            |> Observable.subscribeWithCompletion
                (fun x -> let simulationName = LoadTimeLine.getSimulationName(x.CurrentSimulation)
                          let msg = String.Format("simulation: '{0}', keep concurrent: '{1}', inject per sec: '{2}'",
                                                  simulationName, x.ConstantActorCount, x.OneTimeActorCount)
                          pb.Tick(msg))

                (fun () -> pb.Dispose())

        match dep.ApplicationType with
        | ApplicationType.Console ->
            if scnSchedulers.Length > 1 then
                displayProgressForConcurrentScenarios(scnSchedulers)
            else
                [displayProgressForOneScenario(scnSchedulers.Head)]
        | _ -> List.empty

    let displayConnectionPoolProgress (dep: IGlobalDependency, pool: ConnectionPool) =
        match dep.ApplicationType with
        | ApplicationType.Console ->
            let pb = dep.ProgressBarEnv.CreateManualProgressBar(pool.ConnectionCount)
            pool.EventStream
            |> Observable.subscribeWithCompletion
                (fun event ->
                    match event with
                    | StartedInit poolName -> pb.Message <- sprintf "opening connections for connection pool: '%s'" poolName
                    | StartedStop poolName -> pb.Message <- sprintf "closing connections for connection pool: '%s'" poolName

                    | ConnectionOpened (poolName,number) ->
                        pb.Tick(sprintf "opened connection '%i' for connection pool: '%s'" number poolName)

                    | ConnectionClosed error ->
                        pb.Tick()
                        if error.IsSome then dep.Logger.Error(error.Value.ToString(), "close connection exception")

                    | InitFinished
                    | InitFailed -> pb.Dispose()
                )
                (fun _ -> pb.Dispose())

            |> Some

        | _ -> None

    let printContextInfo (dep) =
        TestHostConfig.printNBomberConfig dep
        TestHostPlugins.printPlugins dep
        TestHostReporting.printReportingSinks dep

module internal TestHostScenario =

    let initConnectionPools (dep: IGlobalDependency) (token: CancellationToken) (pools: ConnectionPool list) =
        taskResult {
            for pool in pools do
                let subscription = TestHostConsole.displayConnectionPoolProgress(dep, pool)
                do! pool.Init(token)
                if subscription.IsSome then subscription.Value.Dispose()

            return pools
        }

    let destroyConnectionPools (dep: IGlobalDependency) (token: CancellationToken) (pools: ConnectionPool list) =
        for pool in pools do
            let subscription = TestHostConsole.displayConnectionPoolProgress(dep, pool)
            pool.Destroy(token)
            if subscription.IsSome then subscription.Value.Dispose()

    let initScenarios (dep: IGlobalDependency,
                       defaultScnContext: ScenarioContext,
                       registeredScenarios: Scenario list,
                       sessionArgs: SessionArgs) = taskResult {

        let tryInitScenario (context: ScenarioContext, initFunc: ScenarioContext -> Task) =
            try
                initFunc(context).Wait()
                Ok()
            with
            | ex -> Error ex

        let rec init (scenarios) = seq {
            match scenarios with
            | scn :: tail ->
                match scn.TestInit with
                | Some initFunc ->
                    dep.Logger.Information("start init scenario: '{Scenario}'", scn.ScenarioName)

                    let context = { defaultScnContext with CustomSettings = scn.CustomSettings }
                    match tryInitScenario(context, initFunc) with
                    | Ok _     -> yield! init(tail)
                    | Error ex -> Error ex

                | None -> Ok()
            | [] -> Ok()
        }

        let targetScenarios =
            registeredScenarios
            |> Scenario.filterTargetScenarios(sessionArgs.TargetScenarios)
            |> Scenario.applySettings(sessionArgs.ScenariosSettings)

        TestHostConsole.printTargetScenarios(dep, targetScenarios)
        do! targetScenarios |> init |> Result.toEmptyIO

        let! pools = targetScenarios
                     |> Scenario.filterDistinctConnectionPoolsArgs
                     |> Scenario.applyConnectionPoolSettings(sessionArgs.ConnectionPoolSettings)
                     |> List.map(fun poolArgs -> new ConnectionPool(poolArgs))
                     |> initConnectionPools dep defaultScnContext.CancellationToken



        let scenariosWithPools = targetScenarios |> Scenario.insertConnectionPools(pools)
        return scenariosWithPools
    }

    let cleanScenarios (dep: IGlobalDependency, defaultScnContext: ScenarioContext, scenarios: Scenario list) =
        scenarios
        |> Scenario.filterDistinctConnectionPools
        |> destroyConnectionPools dep defaultScnContext.CancellationToken

        let tryClean (context: ScenarioContext, cleanFunc: ScenarioContext -> Task) =
            try
                cleanFunc(context).Wait()
            with
            | ex -> dep.Logger.Error(ex.ToString(), "clean scenario failed")

        for s in scenarios do
            match s.TestClean with
            | Some cleanFunc ->
                dep.Logger.Information("start clean scenario: '{Scenario}'", s.ScenarioName)
                let context = { defaultScnContext with CustomSettings = s.CustomSettings }
                tryClean(context, cleanFunc)

            | None -> ()
