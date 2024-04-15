using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using System.Linq;

public class ServerController : NetworkBehaviour
{

    /// <summary>
    /// Store the connected players
    /// </summary>
    private Dictionary<ulong, NetworkObject> playerIDDict;

    /// <summary>
    /// Store the index of the last processed input payload from each client
    /// </summary>
    private Dictionary<ulong, int> lastProcessedIndexes;

    /// <summary>
    /// Stores all of the inputs the players send, to be processed every tick
    /// </summary>
    private Queue<InputPayload> playerInputQueue;

    /// <summary>
    /// Stores all of the states processed by the server, to be sent back to the players at the end of every tick
    /// </summary>
    private Queue<StatePayload> playerStateQueue;

    /// <summary>
    /// List of all of the players that had input processed this tick
    /// </summary>
    private List<ulong> playersActiveThisTick;

    /// <summary>
    /// Allocate memory for a reference to a player object. This will change depending on the player whose inputs are being processed
    /// </summary>
    private NetworkObject playerObjectReference;

    /// <summary>
    /// Reserves memory for this reference, which is reassigned in SendOtherClientsState().
    /// </summary>
    private PlayerInfoPayload otherPlayerInfoReference;

    /// <summary>
    /// If this bool is false, none of the code in this script should be run. This exists because deactivating objects doesn't work, 
    /// because apparently setting [COMPONENT].enabled to false only stops it from running Update(), not Tick(). To stop a part of
    /// this script from running, just add "if (!isActivated) { return; }" at the beginning of the method.
    /// </summary>
    private bool isActivated = true;

    private void Awake()
    {
        //Instantiate data structures
        playerIDDict = new Dictionary<ulong, NetworkObject>();
        lastProcessedIndexes = new Dictionary<ulong, int>();
        playerInputQueue = new Queue<InputPayload>();
        playerStateQueue = new Queue<StatePayload>();
        playersActiveThisTick = new List<ulong>();
    }

    private void Start()
    {
        //DeactivateIfNotHost();
        //playerIDDict.Add(this.OwnerClientId, this.GetComponent<NetworkObject>());
        //playersActiveThisTick.Add(this.OwnerClientId);
    }

    public override void OnNetworkSpawn()
    {
        NetworkManager.NetworkTickSystem.Tick += Tick;
        DeactivateIfNotHost();
        if (!isActivated) { return; }

        playerIDDict.Add(this.OwnerClientId, this.GetComponent<NetworkObject>());
        playersActiveThisTick.Add(this.OwnerClientId);
    }

    private void Tick()
    {
        if (!isActivated) { return; }
        ProcessAndSendInputs();
    }

    public override void OnNetworkDespawn() // don't forget to unsubscribe
    {
        NetworkManager.NetworkTickSystem.Tick -= Tick;
    }

    /// <summary>
    /// Simulates recieved input locally, draining the input queue. Then, creates states for each player
    /// who sent input this tick, and sends them their updated state.
    /// </summary>
    private void ProcessAndSendInputs()
    {

        //process every input in the queue
        for (int i = 0; i < playerInputQueue.Count; i++)
        {
            ProcessInput(playerInputQueue.Dequeue());
        }

        //Create states for each player that had input processed this tick. I use playersActiveThisTick.ToList
        //to create a copy of the list that I iterate through, because removing elements from a list while
        //I'm iterating through the same list causes errors.
        foreach (ulong id in playersActiveThisTick.ToList<ulong>())
        {
            //Create a state for the player and add it to playerStateQueue
            CreateState(id);

            //Remove the player from playerActiveThisTick after their state is created unless it's the host,
            //because the host will always have input to process
            if (id != this.OwnerClientId)
            {
                playersActiveThisTick.Remove(id);
            }
        }


        //Send states back to clients (should every state be sent to every client? or should each state be sent back to its own client, and give the other
        //players less specific information on the other clients? ex. does player 2 need to know that player 1 has an ability primed?)

        //I only need to send each player their most recent state. 
        for (int i = 0; i < playerStateQueue.Count; i++)
        {
            StatePayload sp = playerStateQueue.Dequeue();
            
            //Don't need to send the host an rpc for itself
            if (sp.clientID != this.OwnerClientId)
            {
                SendSingleClientState(sp);
            }
            SendOtherClientsState(sp);
        }

    }

    /// <summary>
    /// Process a singular input payload
    /// </summary>
    /// <param name="input"></param>
    private void ProcessInput(InputPayload input)
    {
        if (!playersActiveThisTick.Contains(input.clientID))
        {
            playersActiveThisTick.Add(input.clientID);
        }

        //Process input from other clients
        if (input.clientID != OwnerClientId)
        {
            //Get a reference to the current player's NetworkObject
            playerObjectReference = playerIDDict[input.clientID];

            //Process player movement
            playerObjectReference.GetComponent<PlayerClientPrediction>().ApplyInput(input);



            //Don't forget to process everything else here



            //Save the index of the most recently processed input payload to send back in the state payload, for reconciliation
            if (lastProcessedIndexes.ContainsKey(input.clientID))
            {
                lastProcessedIndexes[input.clientID] = input.state_Index;
                Debug.Log("lastProcessedIndexes[" + input.clientID + "] = " + input.state_Index);
            }
            else
            {
                Debug.Log("Creating dict entry: " + input.clientID + ": " + input.state_Index);
                lastProcessedIndexes.Add(input.clientID, input.state_Index);
            }
        }

        //Process local input, but this won't actually get run
        else
        {
            this.GetComponent<PlayerClientPrediction>().ApplyInput(input);
        }

    }

    /// <summary>
    /// Create a StatePayload for a client
    /// </summary>
    /// <param name="id"></param>
    private void CreateState(ulong id)
    {
        if (id != 0)
        {
            //Debug.Log(lastProcessedIndexes[id]);
            playerStateQueue.Enqueue(new StatePayload
            {
                playerBodyRotation = NetworkManager.Singleton.ConnectedClients[id].PlayerObject.transform.rotation,
                playerCameraRotation = NetworkManager.Singleton.ConnectedClients[id].PlayerObject.GetComponentInChildren<Camera>().transform.rotation,
                playerPosition = NetworkManager.Singleton.ConnectedClients[id].PlayerObject.transform.position,
                state_Index = lastProcessedIndexes[id],
                clientID = id
            });
        }

        //Don't need a state index for the host, because they won't perform reconciliation.
        else
        {
            playerStateQueue.Enqueue(new StatePayload
            {
                playerBodyRotation = NetworkManager.Singleton.ConnectedClients[id].PlayerObject.transform.rotation,
                playerCameraRotation = NetworkManager.Singleton.ConnectedClients[id].PlayerObject.GetComponentInChildren<Camera>().transform.rotation,
                playerPosition = NetworkManager.Singleton.ConnectedClients[id].PlayerObject.transform.position,
                state_Index = 0,
                clientID = id
            });
        }
        

        
    }

    /// <summary>
    /// Iterates through all clients who aren't the host and aren't the player in the state; Sends them
    /// only what they need to know about other players.
    /// </summary>
    /// <param name="state"></param>
    private void SendOtherClientsState(StatePayload state)
    {
        //Create a playerInfoPayload
        PlayerInfoPayload otherPlayerInfoReference = new PlayerInfoPayload(state, playerIDDict[state.clientID]);

        //Iterate through all players that aren't the one in the state or the host and send them playerInfo
        foreach (ulong id in playerIDDict.Keys)
        {
            //Send the state if it isn't being sent to the host and a player isn't being sent their own state
            if (id != this.OwnerClientId && id != state.clientID)
            {
                SendOtherClientStateRpc(otherPlayerInfoReference, playerIDDict[id], RpcTarget.Single(id, RpcTargetUse.Temp));
            }
            //else { Debug.Log("Not synching state, " + id + " == " + this.OwnerClientId + " and/or " + id + " == " + state.clientID); }
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void SendOtherClientStateRpc(PlayerInfoPayload playerInfo, NetworkObjectReference target, RpcParams rpcParams)
    {
        //If I can reference the target player
        if (target.TryGet(out NetworkObject client))
        {
            client.GetComponent<PlayerClientPrediction>().ProcessOtherPlayerStates(playerInfo);
        }
    }


    /// <summary>
    /// Store recieved input in a queue serverside. This is sent via an RPC from the client.
    /// </summary>
    /// <param name="input"></param>
    public void StoreInput(InputPayload input)
    {
        if (!isActivated) { return; }
        PrivateStoreInput(input);
    }

    private void PrivateStoreInput(InputPayload input)
    {
        //If input hasn't been recieved from this player before, add them to the dict so we have a reference to their player object
        if (!playerIDDict.ContainsKey(input.clientID))
        {
            playerIDDict.Add(input.clientID, NetworkManager.Singleton.ConnectedClients[input.clientID].PlayerObject);
        }

        //Store the input in the queue.
        playerInputQueue.Enqueue(input);
    }

    /// <summary>
    /// Send a StatePayload to the client the state describes.
    /// </summary>
    /// <param name="state"></param>
    private void SendSingleClientState(StatePayload state)
    {
        //Send the rpc to the client id specified in the StatePayload with a reference to the NetworkObject
        SendSingleClientStateRpc(state, playerIDDict[state.clientID], RpcTarget.Single(state.clientID, RpcTargetUse.Temp));


        //RpcTargetUse.Temp means that there is one instance of RpcTargetUse that is repopulated every time I call an RpcTarget method,
        //RpcTargetUse.Persistent means it creates a new instance of RpcTarget that won't be repopulated, so it can be reused
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void SendSingleClientStateRpc(StatePayload state, NetworkObjectReference clientObject, RpcParams rpcParams)
    {
        if (clientObject.TryGet(out NetworkObject obj))
        {
            obj.GetComponent<PlayerClientPrediction>().ProcessStateFromServer(state);
        }
        else
        {
            Debug.Log("NetworkObject reference didn't go through");
        }
    }


    /// <summary>
    /// Checks if the player is the host, and if not, then this script is disabled.
    /// </summary>
    private void DeactivateIfNotHost()
    {
        //Deactivate this if the machine isn't the host or if the object isn't the host's playerObject
        if (!IsHost || !IsOwnedByServer || IsOwnedByServer && !IsHost)
        {
            isActivated = false;
        }
    }

}
