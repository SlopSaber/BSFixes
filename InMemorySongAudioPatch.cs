using HarmonyLib;
using System.Threading.Tasks;
using UnityEngine;

namespace BSFixes
{
    // Custom song audio is loaded as a streamed clip. Starting a streamed clip at
    // an offset, as SongPreviewPlayer.CrossfadeTo does for every level preview,
    // blocks the main thread for about 50 ms while the stream is opened and
    // seeked. Decoding the file during the asynchronous load makes the seek free.
    // Preview and gameplay share the clip through AudioClipAsyncLoader's cache.
    [HarmonyPatch(typeof(MediaAsyncLoader), nameof(MediaAsyncLoader.LoadAudioClipFromFilePathAsync))]
    internal static class DecodedSongAudioPatch
    {
        private static bool Prefix(string filePath, ref Task<AudioClip> __result)
        {
            __result = MediaAsyncLoader.LoadAudioClipAsync(filePath, false);
            return false;
        }
    }
}
