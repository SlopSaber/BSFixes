using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BGLib.JsonExtension;
using HarmonyLib;

namespace BSFixes
{
    // FileStorageExtensions.SaveToJSONFileAsync serializes on the calling
    // thread before its async write. PlayerData is saved from the main thread
    // whenever gameplay setup closes, and with thousands of level stats the
    // JSON costs ~120 ms and ~26 MB there. PlayerDataFileModel already passes
    // a freshly built PlayerSaveData snapshot, so serialize that on a worker
    // and write from the main thread as before. Newer saves of the same file,
    // sync or async, supersede an older pending write.
    //
    // Newtonsoft types are never referenced directly: a loader may give mods
    // their own Newtonsoft copy (InitEditor does), and serializing game save
    // data with it would ignore the game's JSON attributes. The game's own
    // JsonConvert is found through JsonSettings' field type instead.
    [HarmonyPatch(typeof(FileStorageExtensions))]
    internal static class PlayerDataSaveSerializationPatch
    {
        private static readonly Dictionary<string, int> Generations = new Dictionary<string, int>();
        private static readonly object DefaultSettings;
        private static readonly MethodInfo SerializeObject;

        static PlayerDataSaveSerializationPatch()
        {
            try
            {
                FieldInfo field = typeof(JsonSettings).GetField("compactWithDefault", BindingFlags.Public | BindingFlags.Static);
                if (field == null) return;
                Type settingsType = field.FieldType;
                Type convert = settingsType.Assembly.GetType("Newtonsoft.Json.JsonConvert", false);
                SerializeObject = convert?.GetMethod("SerializeObject", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(object), settingsType }, null);
                if (SerializeObject != null) DefaultSettings = field.GetValue(null);
            }
            catch (Exception)
            {
                SerializeObject = null;
            }
        }

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
        private static void SaveSyncPrefix(object[] __args)
        {
            if (__args.Length > 2 && __args[2] is string fileName) NextGeneration(fileName);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(FileStorageExtensions.SaveToJSONFileAsync))]
        private static bool SaveAsyncPrefix(object[] __args, ref Task __result)
        {
            if (__args.Length < 5 || !(__args[2] is string fileName)) return true;
            int generation = NextGeneration(fileName);
            // Only the PlayerData snapshot is known to be detached from live state,
            // and the write must resume on the Unity main-thread context.
            if (SerializeObject == null || !(__args[1] is PlayerSaveData saveData) || !(__args[0] is IFileStorage fileStorage)
                || SynchronizationContext.Current == null) return true;
            object settings = __args[4] ?? DefaultSettings;
            if (settings == null || !SerializeObject.GetParameters()[1].ParameterType.IsInstanceOfType(settings)) return true;

            DetachLiveLists(saveData);
            __result = SerializeThenSave(fileStorage, saveData, fileName, (StoragePreference)__args[3], settings, generation);
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

        private static string Serialize(object obj, object settings)
        {
            try
            {
                return (string)SerializeObject.Invoke(null, new[] { obj, settings });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw exception.InnerException;
            }
        }

        private static async Task SerializeThenSave(IFileStorage fileStorage, object obj, string fileName,
            StoragePreference storageLocation, object settings, int generation)
        {
            string json = await Task.Run(() => Serialize(obj, settings));
            if (!IsCurrent(fileName, generation)) return;
            await fileStorage.SaveFileAsync(fileName, json, storageLocation);
        }
    }
}
