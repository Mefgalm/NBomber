namespace NBomber.Extensions

open System
open System.Collections.Generic
open System.Threading.Tasks

type PushResponse = {
    ClientId: string
    Payload: obj
    ReceivedTime: DateTime
}

type ClientId = string

type PushResponseBuffer() =

    let _lockObj = obj()
    let _awaiters = Dictionary<ClientId, TaskCompletionSource<PushResponse>>()
    let _awaitersBuffer = Dictionary<ClientId, Queue<PushResponse>>()
    let _trueTime = CurrentTime()

    let initBufferForClient (clientId) =
        _awaiters.[clientId] <- TaskCompletionSource<PushResponse>()
        _awaitersBuffer.[clientId] <- Queue<PushResponse>()

    member _.InitBufferForClient(clientId: string) = initBufferForClient(clientId)

    member _.WaitOnPushResponse(clientId: string) =
        lock _lockObj (fun () ->
            let awaiterTsc = TaskCompletionSource<PushResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
            let missedResponses = _awaitersBuffer.[clientId]
            if missedResponses.Count > 0 then
                let response = missedResponses.Dequeue()
                awaiterTsc.SetResult(response)
            else
                _awaiters.[clientId] <- awaiterTsc

            awaiterTsc.Task
        )

    member _.PushResponse(clientId: string, payload: obj) =
        lock _lockObj (fun () ->
            let pushResponse = {
                ClientId = clientId
                Payload = payload
                ReceivedTime = _trueTime.UtcNow
            }

            let missedResponses = _awaitersBuffer.[pushResponse.ClientId]
            missedResponses.Enqueue(pushResponse)

            if _awaiters.ContainsKey(pushResponse.ClientId) then
                let awaiterTsc = _awaiters.[pushResponse.ClientId]
                _awaiters.Remove(pushResponse.ClientId) |> ignore
                let latestResponse = missedResponses.Dequeue()
                awaiterTsc.SetResult(latestResponse)
        )