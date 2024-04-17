using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.UIElements;
using System;
using UnityEngine.Windows;


//Structs

/// <summary>
/// Stores inputs from this frame, as well as the frame's deltaTime (so the input can be processed on the server
/// ), and an index, so that the client can find and replay past inputs during reconciliation, and the owner's
/// clientID.
/// </summary>
public struct InputPayload : INetworkSerializable
{
    public Vector2 mouseLookInput;
    public Vector2 wasdInput;
    public bool jumpInput;
    public bool shiftInput;

    //Used to apply input
    public float clientDeltaTime;

    //We use this index so the client can compare a state sent back by the server to a state stored by the 
    //client
    public int state_Index;

    //It may be more performant networkwise to use something like:
    //var clientId = serverRpcParams.Receive.SenderClientId;
    //in the rpc that sends the input, a ulong is a pretty big data type
    public ulong clientID;

    //I have to serialize these values before I can send them to the server in an RPC
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref mouseLookInput);
        serializer.SerializeValue(ref wasdInput);
        serializer.SerializeValue(ref jumpInput);
        serializer.SerializeValue(ref shiftInput);
        serializer.SerializeValue(ref clientDeltaTime);
        serializer.SerializeValue(ref state_Index);
        serializer.SerializeValue(ref clientID);
    }

    
}

/// <summary>
/// Stores transform info about this player, as well as an index so that past predicted states can be
/// compared to states sent back by the server, and the owner's clientID.
/// </summary>
public struct StatePayload : INetworkSerializable
{
    public Quaternion playerBodyRotation;
    public Quaternion playerCameraRotation;
    public Vector3 playerPosition;
    public int state_Index;
    public ulong clientID;

    /// <summary>
    /// Fills all of the fields of StatePayload except state_Index, which is given a value of -1.
    /// </summary>
    /// <param name="obj"></param>
    public StatePayload(NetworkObject obj)
    {
        playerBodyRotation = obj.transform.rotation;
        playerCameraRotation = obj.GetComponentInChildren<Camera>().transform.rotation;
        playerPosition = obj.transform.position;
        clientID = obj.OwnerClientId;

        //Remember that this field isn't filled
        state_Index = -1;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref playerBodyRotation);
        serializer.SerializeValue(ref playerCameraRotation);
        serializer.SerializeValue(ref playerPosition);
        serializer.SerializeValue(ref state_Index);
        serializer.SerializeValue(ref clientID);
    }
}

/// <summary>
/// Smaller version of StatePayload, to be sent to all other players, so they can have info on this player. 
/// Contains a reference to this NetworkObject, as well as various transforms.
/// </summary>
public struct PlayerInfoPayload : INetworkSerializable
{
    public Quaternion playerBodyRotation;
    public Quaternion playerCameraRotation;
    public Vector3 playerPosition;
    public NetworkObjectReference networkObject;
    public ulong clientID;

    public PlayerInfoPayload(StatePayload state, NetworkObject obj)
    {
        playerBodyRotation = state.playerBodyRotation;
        playerCameraRotation = state.playerCameraRotation;
        playerPosition = state.playerPosition;
        networkObject = obj;
        clientID = state.clientID;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref playerBodyRotation);
        serializer.SerializeValue(ref playerCameraRotation);
        serializer.SerializeValue(ref playerPosition);
        serializer.SerializeValue(ref networkObject);
        serializer.SerializeValue(ref clientID);
    }
}

public class PlayerClientPrediction : NetworkBehaviour
{


    /// <summary>
    /// Increment to send and check game states with. Incremented in StoreLocalState().
    /// </summary>
    private int stateIndex = 0;

    //Dictionary to store states in
    //private Dictionary<int, StatePayload> gameStateDictionary = new Dictionary<int, StatePayload>();
    private StatePayload[] pastGameStates;
    private InputPayload[] pastInputStates;
    private const int BUFFERSIZE = 1024;

    /// <summary>
    /// The index of the most recently recorded InputPayload. Referenced in Reconcile().
    /// </summary>
    private int mostRecentInputPayload;

    //fields for interpolation
    /// <summary>
    /// Stores an array[2] for every other player, that contains their last two PlayerInfoPayloads as sent by the server.
    /// </summary>
    private Dictionary<ulong, PlayerInfoPayload[]> otherPlayerStates = new Dictionary<ulong, PlayerInfoPayload[]>();

    /// <summary>
    /// Counts the time it's been since the last bit of info was recieved from the server's last tick.
    /// </summary>
    public float interpolationTime = 0;


    //Component References
    private PlayerController playerController;
    private AbilityController abilityController;
    private FPSController fpsController;
    private MouseLook mouseLook;
    private Transform cam;

    //reference control scheme
    private InputMaster controls;

    //this reference comes from an RPC
    private NetworkObject serverPlayer;


    private void Awake()
    {
        //initialize controls
        controls = new InputMaster();

        //Assign camera reference
        cam = transform.GetComponentInChildren<Camera>().transform;
        

    }

    private void Start()
    {
        if (!IsOwner)
        {
            this.enabled = false;
        }
        pastInputStates = new InputPayload[BUFFERSIZE];
        pastGameStates = new StatePayload[BUFFERSIZE];

        //assign references to components
        playerController = GetComponent<PlayerController>();
        abilityController = GetComponent<AbilityController>();
        fpsController = GetComponent<FPSController>();
        mouseLook = GetComponentInChildren<MouseLook>();

        if (IsHost)
        {
            serverPlayer = this.GetComponent<NetworkObject>();
        }
        else
        {
            HostReferenceRpc();
        }
    }

    //GENIUS IDEA: 
    //Take the code that's currently inside the update function in every other player script out and 
    //put it in a new, public function for that script. This script will call that function in the Update
    //function, so that way I can gather and send the same input info to the RPC and to the local functions
    //at the same time, and control the order the other scripts run in

    void Update()
    {
        //capture input
        InputPayload input = CollectInput();

        //Record and send input if the player isn't the host
        if (!IsHost)
        {
            //Process the collected input locally
            ApplyInput(input);

            //Create a state
            StatePayload currentState = CreateLocalState();

            //Send the input to the server
            PlayerInputRPC(input, new PlayerInfoPayload(currentState, GetComponent<NetworkObject>()));

            //Store the state
            StoreLocalState(currentState, input);

            //interpolate other players' states
            InterpolateOtherPlayers(input.clientDeltaTime);
        }

        //otherwise, just move the host and interpolate the other players
        else
        {
            ApplyInput(input);

            InterpolateOtherPlayers(input.clientDeltaTime);
        }

    }

    /// <summary>
    /// Gathers relevant input from this frame and returns it as an InputPayload.
    /// </summary>
    /// <returns></returns>
    private InputPayload CollectInput()
    {
        //set the isShiftHeld variable to a bool to send in the payload
        bool isShiftHeld = false;
        if (controls.Player.Sprint.ReadValue<float>() > 0.1)
        {
            isShiftHeld = true;
        }

        return new InputPayload
        {
            mouseLookInput = controls.Player.Look.ReadValue<Vector2>(),
            wasdInput = controls.Player.Movement.ReadValue<Vector2>(),
            jumpInput = controls.Player.Jump.triggered,
            shiftInput = isShiftHeld,
            clientDeltaTime = Time.deltaTime,

            state_Index = stateIndex,

            //Possible bandwidth optimization: See note in InputPayload definition
            clientID = OwnerClientId
        };
    }


    /// <summary>
    /// Creates a StatePayload for this frame
    /// </summary>
    /// <returns></returns>
    private StatePayload CreateLocalState()
    {
        return new StatePayload
        {
            playerBodyRotation = transform.rotation,
            playerCameraRotation = cam.transform.rotation,
            playerPosition = transform.position,
            state_Index = stateIndex,

            //Does this really need to be here?
            clientID = OwnerClientId
            
        };
    }

    /// <summary>
    /// Stores the StatePayload and InputPayload from this frame for later reference.
    /// </summary>
    /// <param name="gameState"></param>
    /// <param name="inputState"></param>
    private void StoreLocalState(StatePayload gameState, InputPayload inputState)
    {
        //gameStateDictionary.Add(stateIndex, gameState);
        pastGameStates[stateIndex] = gameState;

        pastInputStates[stateIndex] = inputState;

        //Increment stateIndex
        stateIndex = incrementStateIndex(stateIndex, BUFFERSIZE);
    }

    /// <summary>
    /// Increments a stateIndex with respect to the size of a circular buffer. The buffer!
    /// </summary>
    /// <param name="index">The index to increment.</param>
    /// <param name="bufferSize">The size of the circular buffer.</param>
    /// <returns>The incremented index.</returns>
    public int incrementStateIndex(int index, int bufferSize)
    {
        return (index + 1) % bufferSize;
    }

    /// <summary>
    /// Apply an InputPayload to a player object.
    /// </summary>
    /// <param name="input"></param>
    public void ApplyInput(InputPayload input)
    {
        //Player movement
        playerController.MovementUpdate(input.clientDeltaTime, input.wasdInput, input.jumpInput, input.shiftInput);

        //Player camera movement
        mouseLook.LookUpdate(input.mouseLookInput, input.clientDeltaTime);
    }

    /*
    /// <summary>
    /// Same as ApplyInput, but instead of actually applying the input to the object, it's returned as
    /// a StatePayload.
    /// </summary>
    /// <param name="input">The InputPayload to "apply."</param>
    /// <param name="obj">The player object to "apply" the InputPayload to.</param>
    /// <returns>The state of the player object, if the InputPayload were actually applied to it.</returns>
    public static StatePayload ConvertInput(InputPayload input, NetworkObject obj)
    {
        
    }*/

    /// <summary>
    /// Fetch the server NetworkObject reference
    /// </summary>
    [Rpc(SendTo.Server)]
    private void HostReferenceRpc()
    {
        StoreHostReferenceRpc(NetworkManager.Singleton.ConnectedClients[0].PlayerObject);
    }

    [Rpc(SendTo.Me)]
    private void StoreHostReferenceRpc(NetworkObjectReference hostObject)
    {
        //This is how you pass NetworkObjects over RPCs
        if (hostObject.TryGet(out NetworkObject reference)){
            serverPlayer = reference;
            Debug.Log("This is good");
        }

        else
        {
            Debug.Log("Ruh roh");
        }
    }


    /// <summary>
    /// Send input from this frame to the server. Run interpolation code on it as well.
    /// </summary>
    /// <param name="inputPayload"></param>
    [Rpc(SendTo.Server)]
    private void PlayerInputRPC(InputPayload inputPayload, PlayerInfoPayload state)
    {
        //process input (copy pasted functions from other scripts?)
        serverPlayer.GetComponent<ServerController>().StoreInput(inputPayload);
        //serverPlayer.GetComponent<PlayerClientPrediction>().AddStateToPlayerDict(state);
    }

    /// <summary>
    /// Process info sent by other players locally. This is called by SendOtherClientStateRpc in ServerController.
    /// </summary>
    /// <param name="info"></param>
    public void ProcessOtherPlayerStates(PlayerInfoPayload info)
    {
        //If I can reference the player I'm going to move
        if (info.networkObject.TryGet(out NetworkObject otherPlayer))
        {
            AddStateToPlayerDict(info);
        }
    }


    /// <summary>
    /// Process the state sent back by the server locally.
    /// </summary>
    public void ProcessStateFromServer(StatePayload state)
    {
        if (CompareStatePayloads(pastGameStates[state.state_Index], state))
        {
            //Everything is working as it should be; the states don't match because the client isn't predicting
            //anything.
            Debug.Log("The states match!");
        }
        else
        {
            //Reconcile(state, stateIndex);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="localState">A state matching the other state from the locally stored list of previous states.</param>
    /// <param name="other">The state sent back by the server.</param>
    /// <returns></returns>
    private bool CompareStatePayloads(StatePayload localState, StatePayload other)
    {
        if (localState.Equals(other))
        {
            return true;
        }

        else
        {
            if (!localState.playerPosition.Equals(other.playerPosition))
            {
                Debug.Log("PlayerPos mismatch: " + localState.playerPosition + "!=" + other.playerPosition);
            }

            if (!localState.playerBodyRotation.Equals(other.playerBodyRotation))
            {
                Debug.Log("PlayerBodyRotation mismatch: " + localState.playerPosition + "!=" + other.playerPosition);
            }

            if (!localState.playerCameraRotation.Equals(other.playerCameraRotation))
            {
                Debug.Log("PlayerCameraRotation mismatch: " + localState.playerPosition + "!=" + other.playerPosition);
            }

            return false;
        }
    }

    /// <summary>
    /// Runs when the client-predicted state and the state sent by the server don't match up. The client accepts the server's state
    /// and runs all of the input recorded after the mismatch based on the new state.
    /// </summary>
    /// <param name="state">The state sent by the server.</param>
    /// <param name="inputIndex">The index of the most recent InputPayload.</param>
    private void Reconcile(StatePayload state, int inputIndex)
    {
        Debug.Log("Reconciling");
        //apply the state the server sent (going back in time here)
        ApplyPastState(state.state_Index);

        //replay all of the following input
        for (int i = state.state_Index; i == inputIndex; i = incrementStateIndex(i, BUFFERSIZE))
        {
            Debug.Log("Replaying input index " + i + ", stopping at index " + inputIndex);
            //Apply input for the equivalent of i + 1
            ApplyInput(pastInputStates[incrementStateIndex(i, BUFFERSIZE)]);
        }
    }

    /// <summary>
    /// Returns the client to the state with the index given. Doesn't change stateIndex, but changes elements in pastGameStates. Used in Reconcile().
    /// </summary>
    /// <param name="index">Index of the state (in pastGameStates) to apply. </param>
    private void ApplyPastState(int index)
    {
        //apply state
        PlayerClientPrediction.ApplyState(this.GetComponent<NetworkObject>(), pastGameStates[index]);

        //change the past...
        pastGameStates[index] = CreateLocalState();
    }

    /// <summary>
    /// Sets a player object to a state.
    /// </summary>
    /// <param name="obj">The target player object.</param>
    /// <param name="state">The state to apply to the object.</param>
    public static void ApplyState(NetworkObject obj, StatePayload state)
    {
        obj.transform.position = state.playerPosition;
        obj.transform.rotation = state.playerBodyRotation;
        obj.GetComponentInChildren<Camera>().transform.rotation = state.playerCameraRotation;
    }

    /// <summary>
    /// Stores the most recent PlayerInfoPayload for another player as sent by the server in otherPlayerStates, so this player can interpolate their actions.
    ///  If the other player doesn't have an entry in the dict yet, one is created. Also resets interpolationTime.
    /// </summary>
    /// <param name="other"></param>
    public void AddStateToPlayerDict(PlayerInfoPayload state)
    {
        //create an entry for the player if one doesn't exist
        if (!otherPlayerStates.ContainsKey(state.clientID))
        {
            //create the array to be the value for the dict and set the most recent state to the one being processed.
            //this is so that when the rest of the function runs, both stateArray[0] and stateArray[1] will be the same
            //value, so I can interpolate between them without a nullReferenceException.
            PlayerInfoPayload[] stateArray = new PlayerInfoPayload[2];
            stateArray[0] = state;
            
            otherPlayerStates.Add(state.clientID, stateArray);
        }

        //shift back the older state, and add the new one
        if (otherPlayerStates.TryGetValue(state.clientID, out PlayerInfoPayload[] array))
        {
            array[1] = array[0];
            array[0] = state;
        }


        //reset interpolationTime
        Debug.Log("Resetting interpolationTime");
        interpolationTime = 0;
    }

    /// <summary>
    /// Interpolates all other players based on their past 2 states stored in otherPlayerStates and interpolationTime. If 
    /// interpolationTime > the server's tick rate, the other players will not move. Increments interpolationTime.
    /// </summary>
    private void InterpolateOtherPlayers(float deltaTime)
    {
        interpolationTime += deltaTime;
        //Debug.Log("Interpolation time = " + interpolationTime);
        Debug.Log("Starting interpolation process");
        foreach (ulong id in otherPlayerStates.Keys)
        {
            if (otherPlayerStates.TryGetValue(id, out PlayerInfoPayload[] states))
            {
                //reference their NetworkObject. Should I also have a Dictionary<ulong, NetworkObject>? probably.
                if (states[0].networkObject.TryGet(out NetworkObject otherPlayer))
                {
                    Debug.Log("Interpolating: t = " + (float) interpolationTime / (1f / 25f));
                    //interpolate movement
                    otherPlayer.transform.position = Vector3.Lerp(states[1].playerPosition, states[0].playerPosition, interpolationTime / (1f / 25f));
                    otherPlayer.transform.rotation = Quaternion.Lerp(states[1].playerBodyRotation, states[0].playerBodyRotation, interpolationTime / (1 / 25));
                    otherPlayer.GetComponentInChildren<Camera>().transform.rotation = Quaternion.Lerp(
                        states[1].playerCameraRotation, states[0].playerCameraRotation, interpolationTime / (1f / 25f));

                    //interpolate other stuff here
                    
                }
                else
                {
                    Debug.Log("Couldn't reference networkObject of player " + id);
                }
            }
        }
        
    }


    //prevents memory leaks, supposedly
    private void OnEnable()
    {
        controls.Enable();
    }
    private void OnDisable()
    {
        controls.Disable();
    }

}
