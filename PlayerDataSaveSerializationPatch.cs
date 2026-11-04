using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BGLib.JsonExtension;
using HarmonyLib;
using Newtonsoft.Json;

namespace BSFixes
{
    // FileStorageExtensions.SaveToJSONFileAsync serializes on the calling
    // thread before its async write. PlayerData is saved from the main thread
    // whenever gameplay setup closes, and with thousands of level stats the
    // JSON costs ~120 ms and ~26 MB there. PlayerDataFileModel already passes
    // a freshly built PlayerSaveData snapshot, so serialize that on a worker
    // and write from the main thread as before. Newer saves of the same file,
    // sync or async, supersede an older pending write.
    [HarmonyPatch(typeof(FileStorageExtensions))]
    internal static class PlayerDataSaveSerializationPatch
    {
        private static readonly Dictionary<string, int> Generations = new Dictionary<string, int>();

        private static int NextGeneration(string fileName)
        {
            lock (Generations)
            {
                Generations.TryGetValue(fileName, out int generation);
                Generations[fileName] = ++generation;
                return generation;
            }
        }

        private static bool IsCurrent(string fileName, int generation)
        {
            lock (Generations)
                return Generations.TryGetValue(fileName, out int current) && current == generation;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(FileStorageExtensions.SaveToJSONFile))]
        private static void SaveSyncPrefix(string fileName)
        {
            if (fileName != null) NextGeneration(fileName);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(FileStorageExtensions.SaveToJSONFileAsync))]
        private static bool SaveAsyncPrefix(IFileStorage fileStorage, object obj, string fileName, StoragePreference storageLocation,
            JsonSerializerSettings overrideSerializerSettings, ref Task __result)
        {
            if (fileName == null) return true;
            int generation = NextGeneration(fileName);
            // Only the PlayerData snapshot is known to be detached from live state,
            // and the write must resume on the Unity main-thread context.
            if (!(obj is PlayerSaveData saveData) || SynchronizationContext.Current == null) return true;

            DetachLiveLists(saveData);
            __result = SerializeThenSave(fileStorage, saveData, fileName, storageLocation,
                overrideSerializerSettings ?? JsonSettings.compactWithDefault, generation);
            return false;
        }

        private static void DetachLiveLists(PlayerSaveData saveData)
        {
            // GetPlayerSaveData copies everything except this list reference.
            if (saveData.localPlayers == null) return;
            foreach (PlayerSaveData.LocalPlayer player in saveData.localPlayers)
                if (player?.showedMissionHelpIds != null)
                    player.showedMissionHelpIds = new List<string>(player.showedMissionHelpIds);
        }

        private static async Task SerializeThenSave(IFileStorage fileStorage, object obj, string fileName,
            StoragePreference storageLocation, JsonSerializerSettings settings, int generation)
        {
            string json = await Task.Run(() => JsonConvert.SerializeObject(obj, settings));
            if (!IsCurrent(fileName, generation)) return;
            await fileStorage.SaveFileAsync(fileName, json, storageLocation);
        }
    }
}
