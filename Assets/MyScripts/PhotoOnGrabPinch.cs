using UnityEngine;
using Valve.VR;

public class PhotoOnGrabPinch : MonoBehaviour
{
    [SerializeField] private MJPEGStreamRenderer stream;
    [SerializeField] private SteamVR_Action_Boolean takePhotoAction = SteamVR_Input.GetAction<SteamVR_Action_Boolean>("PhotoGrabGrip");
    [SerializeField] private SteamVR_Input_Sources hand = SteamVR_Input_Sources.Any; // например RightHand Ч только правый курок
    [SerializeField] private AudioSource shutterSound; 
    [SerializeField] private float cooldown = 0.5f;

    private float nextAllowedTime;

    void Awake()
    {
        if (stream == null)
            stream = FindObjectOfType<MJPEGStreamRenderer>();
    }

    void Update()
    {
        if (stream == null || takePhotoAction == null)
            return;

        if (takePhotoAction.GetStateDown(hand) && Time.unscaledTime >= nextAllowedTime)
        {
            nextAllowedTime = Time.unscaledTime + cooldown;
            stream.TakePhoto();
            if (shutterSound != null)
                shutterSound.Play();
        }
    }
}
