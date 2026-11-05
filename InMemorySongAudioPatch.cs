using HarmonyLib;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace BSFixes
{
    // Custom song audio is loaded as a streamed clip. Starting a streamed clip at
    // an offset, as SongPreviewPlayer.CrossfadeTo does for every level preview,
    // blocks the main thread for about 50 ms while the stream is opened and
    // seeked. Decompressing on load instead decodes the whole song on the main
    // thread when the clip is created. Keeping the compressed data in memory
    // avoids both: creation only wraps the bytes, and seeks read from memory.
    // Preview and gameplay share the clip through AudioClipAsyncLoader's cache.
    [HarmonyPatch(typeof(MediaAsyncLoader), nameof(MediaAsyncLoader.LoadAudioClipFromFilePathAsync))]
    internal static class InMemorySongAudioPatch
    {
        private static bool Prefix(string filePath, ref Task<AudioClip> __result)
        {
            __result = LoadCompressedInMemory(filePath);
            return false;
        }

        private static async Task<AudioClip> LoadCompressedInMemory(string filePath)
        {
            using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(
                FileHelpers.GetEscapedURLForFilePath(filePath), AudioTypeHelper.GetAudioTypeFromPath(filePath));
            var handler = (DownloadHandlerAudioClip)request.downloadHandler;
            handler.streamAudio = false;
            handler.compressed = true;
            var completion = new TaskCompletionSource<bool>();
            request.SendWebRequest().completed += _ => completion.TrySetResult(true);
            await completion.Task;
            return request.result == UnityWebRequest.Result.Success ? DownloadHandlerAudioClip.GetContent(request) : null;
        }
    }
}
