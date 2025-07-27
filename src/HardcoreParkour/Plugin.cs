﻿using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HardcoreParkour;

// Game Notes:
//
// The current character ragdoll switches between a kinematic and non-kinematic
// version depending on a 0 or 1 value.
//   0 is falling/passed out/etc. (ragdoll is kinematic)
//   1 is standing (ragdoll is non-kinematic)
[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static ConfigEntry<KeyCode>? keyboardKeybindConfig;
    internal static ConfigEntry<KeyCode>? controllerKeybindConfig;
    internal static ConfigEntry<bool>? debuggingLogsConfig;

    const float launchForceForward = 200f;
    const float launchForceUp = 200f;
    const float initialTorque = 50f;
    const float continuousTorque = 20f;
    const float continuousForceUp = 1.75f;
    const float flipLandedVerticalDotThreshold = 0.6f;
    const float flipDirectionScalingMin = 0.8f;
    const float flipHeightScalingMax = 1.5f;
    const float jumpMaxHeightTime = 0.3f;

    bool groundedSinceLastFlip = true;
    float flipStartTime = 0f;
    float lastGroundedTime = 0f;
    Vector3 flipStartYawForward = Vector3.zero;
    Vector3 flipStartYawRight = Vector3.zero;

    // Reference transform that only follows camera yaw (horizontal rotation).
    private Transform? yawReferenceTransform;

    private void Awake()
    {
        // Log our awake here so we can see it in LogOutput.log file
        Log = Logger;
        Log.LogInfo($"Plugin {Name} is loaded!");

        // Create a yaw reference transform to keep track of flip direction.
        GameObject yawReference = new GameObject("HardcoreParkour_YawReference");
        yawReferenceTransform = yawReference.transform;
        DontDestroyOnLoad(yawReference);

        keyboardKeybindConfig = Config.Bind("Settings", "Keyboard Keybind", KeyCode.F, "Keyboard key used to trigger flips. Defaults to F.");
        controllerKeybindConfig = Config.Bind("Settings", "Controller Keybind", KeyCode.JoystickButton5, "Controller button used to trigger flips. Defaults to JoystickButton5 (RB).");
        debuggingLogsConfig = Config.Bind("Debugging", "Enable Logs", false, "Enable logs for debugging.");
    }

    private void RotateRagdoll(CharacterRagdoll ragdoll, Vector3 movementDirection, float torque, ForceMode forceMode = ForceMode.Impulse)
    {
        foreach (Rigidbody rb in ragdoll.rigidbodies)
        {
            // The axis to flip around is perpendicular to movement and up (cross product)
            Vector3 flipAxis = -Vector3.Cross(movementDirection, Vector3.up).normalized;
            rb.AddTorque(flipAxis * torque, forceMode);
        }
    }

    // Update the yaw reference transform to match camera's horizontal rotation only
    private void UpdateYawReference()
    {
        if (MainCamera.instance != null && yawReferenceTransform != null)
        {
            // Get the camera's current rotation
            Vector3 cameraEuler = MainCamera.instance.transform.eulerAngles;

            // Only use the yaw (Y rotation), set pitch and roll to 0
            yawReferenceTransform.rotation = Quaternion.Euler(0f, cameraEuler.y, 0f);
        }
    }

    // Determine flip direction based on input keys (WASD), using a stable yaw-only reference.
    private Vector3 GetFlipDirection()
    {
        Character localCharacter = Character.localCharacter;
        if (localCharacter == null || yawReferenceTransform == null) return Vector3.zero;

        // Use the yaw reference transform instead of the camera transform,
        // ensuring forward/right are always world-horizontal.
        Vector3 flatForward = flipStartYawForward;
        Vector3 flatRight = flipStartYawRight;

        Vector3 direction = Vector3.zero;

        // Keyboard input.
        if (Input.GetKey(KeyCode.W))
            direction += flatForward;
        if (Input.GetKey(KeyCode.S))
            direction -= flatForward;
        if (Input.GetKey(KeyCode.D))
            direction += flatRight;
        if (Input.GetKey(KeyCode.A))
            direction -= flatRight;

        // Controller input.
        if (Input.GetAxis("Horizontal") != 0)
        {
            direction += flatRight * Input.GetAxis("Horizontal");
        }
        if (Input.GetAxis("Vertical") != 0)
        {
            direction += flatForward * Input.GetAxis("Vertical");
        }

        return direction.sqrMagnitude > 0f ? direction.normalized : Vector3.zero;
    }

    private void Update()
    {
        Character localCharacter = Character.localCharacter;
        if (localCharacter == null || !Application.isFocused || yawReferenceTransform == null) return;

        if (localCharacter.data.isGrounded)
        {
            lastGroundedTime = Time.time;

            // Do something if they landed the flip.
            if (!groundedSinceLastFlip && (Time.time - flipStartTime > .5f))
            {
                // Check if the character's "up" direction is close to world up (upright).
                // We'll do this for both feet to see if either is upright enough.
                Transform footLTransform = localCharacter.GetBodypart(BodypartType.Foot_L).rig.transform;
                Transform footRTransform = localCharacter.GetBodypart(BodypartType.Foot_R).rig.transform;
                // We take the negative of each Dot product because the up vector seems to be pointing down.
                float uprightDotL = -Vector3.Dot(footLTransform.up, Vector3.up);
                float uprightDotR = -Vector3.Dot(footRTransform.up, Vector3.up);
                if (debuggingLogsConfig!.Value)
                {
                    Log.LogInfo($"Upright dots: {uprightDotL}, {uprightDotR}");
                }
                // Only consider upright if the dot is positive (not upside down) and above the threshold.
                if ((uprightDotL > flipLandedVerticalDotThreshold) || (uprightDotR > flipLandedVerticalDotThreshold))
                {
                    if (debuggingLogsConfig!.Value)
                    {
                        Log.LogInfo($"Landed the flip!");
                    }

                    // Reset the fall animations.
                    localCharacter.data.fallSeconds = 0f;
                    localCharacter.data.currentRagdollControll = Mathf.MoveTowards(localCharacter.data.currentRagdollControll, 1, Time.fixedDeltaTime * 100f);

                    foreach (Bodypart bodypart in localCharacter.refs.ragdoll.partList)
                    {
                        bodypart.rig.linearVelocity *= 0.5f;
                        bodypart.rig.angularVelocity = Vector3.zero;
                        bodypart.rig.MoveRotation(Quaternion.LookRotation(bodypart.rig.rotation * Vector3.forward, bodypart.targetUp));
                    }

                    // If they landed the flip, reward them with full stamina.
                    Character.GainFullStamina();
                }
            }

            groundedSinceLastFlip = true;
            flipStartTime = 0f;
        }

        // Update the yaw reference to match current camera yaw
        UpdateYawReference();

        float jumpPeakAmount = Mathf.Lerp(0, 1, (Time.time - lastGroundedTime) / jumpMaxHeightTime);
        float jumpHeightScale = Mathf.Lerp(1f, flipHeightScalingMax, jumpPeakAmount);

        // Flip if:
        // - The flip button is pressed.
        // - The character is already off the ground.
        // - The character has landed since they last flipped.
        if ((Input.GetKeyDown(keyboardKeybindConfig!.Value) || Input.GetKeyDown(controllerKeybindConfig!.Value))
            && !localCharacter.data.isGrounded && groundedSinceLastFlip)
        {
            flipStartTime = Time.time;

            // Toggle the ragdoll state.
            localCharacter.photonView.RPC("RPCA_Fall", RpcTarget.All, .5f);

            flipStartYawForward = yawReferenceTransform.forward;
            flipStartYawRight = yawReferenceTransform.right;

            Vector3 flipDirection = GetFlipDirection();

            // Scale launch forces based on how much the flip direction is facing away from the yaw reference forward.
            // If flipDirection is opposite of yaw reference forward, scale down the force.
            float forwardDot = Vector3.Dot(flipDirection.normalized, flipStartYawForward.normalized);

            // forwardDot: 1 = forward, 0 = sideways, -1 = backward
            // Remap forwardDot from [-1, 1] to [0, 1] range
            float forwardDotRemapped = Mathf.InverseLerp(-1f, 1f, forwardDot);

            // We'll scale the force so that it's 100% when forward, 50% when sideways, 30% when fully backward.
            float forwardScale = Mathf.Lerp(flipDirectionScalingMin, 1f, Mathf.Max(0f, forwardDot));
            float dirScaledLaunchForceForward = launchForceForward * forwardScale;

            // Add force in the direction that they're already moving.
            localCharacter.AddForce(flipDirection * jumpHeightScale * dirScaledLaunchForceForward);

            // Add a small impulse force upwards to help them flip, but only if
            // they delay their jump long enough.
            if (jumpPeakAmount > 0.6f)
            {
                if (debuggingLogsConfig!.Value)
                {
                    Log.LogInfo($"Jumped high enough for the helper force.");
                }
                localCharacter.AddForce(Vector3.up * launchForceUp);
            }

            // Flipping early while jumping backwards causing a huge amount of
            // torque for some reason, so we compute a scale factor to work
            // against that.zc
            // Early in the jump: jumpPeakAmount is near 0, so scale is low.
            // Jumping backwards: forwardDot is near -1, so scale is low.
            float backwardScale = Mathf.Lerp(0.3f, 1f, forwardDotRemapped); // 0.3 when backward, 1 when forward
            float earlyJumpScale = Mathf.Lerp(0.1f, 4f, jumpPeakAmount); // 0.5 at jump start, 1 at peak
            // float earlyJumpScale = backwardScale == 1 ? 1 : Mathf.Lerp(0.5f, 4f, jumpPeakAmount); // 0.5 at jump start, 1 at peak
            float jumpBackwardScale = earlyJumpScale * backwardScale;

            if (debuggingLogsConfig!.Value)
            {
                Log.LogInfo($"forwardDotRemapped: {forwardDotRemapped} Jump height scale: {jumpHeightScale} jumpPeakAmount: {jumpPeakAmount} backwardScale: {backwardScale} earlyJumpScale: {earlyJumpScale}");
            }

            float finalInitialTorque = initialTorque * jumpHeightScale * jumpBackwardScale;

            if (debuggingLogsConfig!.Value)
            {
                Log.LogInfo($"Final initial torque: {finalInitialTorque}");
            }
            RotateRagdoll(localCharacter.refs.ragdoll, flipDirection, finalInitialTorque);

            groundedSinceLastFlip = false;
        }
        // Keep flipping if they've started a flip and are not grounded.
        else if (flipStartTime > 0f && !groundedSinceLastFlip)
        {
            Vector3 flipDirection = GetFlipDirection();

            // Only flip if they're in holding a direction.
            if (flipDirection != Vector3.zero)
            {
                float jumpHeightFactor = Mathf.Lerp(0.5f, 1f, jumpPeakAmount);
                float finalContinuousTorque = continuousTorque * jumpHeightFactor;

                RotateRagdoll(localCharacter.refs.ragdoll, flipDirection, finalContinuousTorque, ForceMode.Acceleration);

                // Add a small force upwards to help them stay in the air.
                localCharacter.AddForce(Vector3.up * continuousForceUp);
            }
        }


    }
}
