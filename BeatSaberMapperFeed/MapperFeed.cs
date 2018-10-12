﻿using SongLoaderPlugin;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using UnityEngine;
using UnityEngine.Networking;

namespace BeatSaberMapperFeed {
    class MapperFeed : MonoBehaviour {
        private bool _downloaderRunning = false;
        private Stack<string> _authorDownloadQueue = new Stack<string>();
        private List<string> _songDownloadHistory = new List<string>();

        private string _historyPath = null;

        void Awake() {
            UnityEngine.Object.DontDestroyOnLoad(this.gameObject);

            _historyPath = $"{Environment.CurrentDirectory}\\UserData\\MapperFeedHistory.txt";
            if (File.Exists(_historyPath)) {
                _songDownloadHistory = File.ReadAllLines(_historyPath).ToList();
            }

            string favoriteMappersPath = $"{Environment.CurrentDirectory}\\UserData\\FavoriteMappers.ini";
            if (!File.Exists(favoriteMappersPath)) {
                File.WriteAllLines(favoriteMappersPath, new string[] { "freeek", "purphoros", "bennydabeast" });
            }
            
            foreach (string mapper in File.ReadAllLines(favoriteMappersPath)) {
                Plugin.Log($"Mapper: {mapper}");
                _authorDownloadQueue.Push(mapper);
            }
        }

        void Update() {
            if (_authorDownloadQueue.Count > 0 && !_downloaderRunning) {
                StartCoroutine(DownloadAllSongsByAuthor(_authorDownloadQueue.Pop()));
            }
            else if (_authorDownloadQueue.Count == 0 && !_downloaderRunning) {
                // Write our download history to file
                _songDownloadHistory.Sort();
                File.WriteAllLines(_historyPath, _songDownloadHistory);

                SongLoader.Instance.RefreshSongs(false);
                Plugin.Log("Finished updating songs from all mappers!");
                Destroy(this.gameObject);
            }
        }

        public void Empty(System.IO.DirectoryInfo directory) {
            foreach (System.IO.FileInfo file in directory.GetFiles()) file.Delete();
            foreach (System.IO.DirectoryInfo subDirectory in directory.GetDirectories()) subDirectory.Delete(true);
        }
        
        string GetAuthorID(string author, byte[] rawData) {
            string searchResult = System.Text.Encoding.Default.GetString(rawData);
            string search = $">{author.ToLower()}<";

            while (true) {
                int index = searchResult.IndexOf(search);
                if (index != -1) {
                    string tmp = searchResult.Substring(0, index - 1);
                    return tmp.Substring(tmp.LastIndexOf('/') + 1);
                }
                else {
                    break;
                }
            }
            return String.Empty;
        }

        IEnumerator DownloadAllSongsByAuthor(string author) {
            _downloaderRunning = true;
            using (UnityWebRequest www = UnityWebRequest.Get($"https://beatsaver.com/search/all/{author}")) {
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError) {
                    Plugin.Log(www.error);
                }
                else {
                    byte[] data = www.downloadHandler.data;
                    string authorID = GetAuthorID(author, data);
                    if (authorID != String.Empty) {
                        yield return DownloadSongs(author, authorID);
                    }
                }
            }
            _downloaderRunning = false;
        }

        IEnumerator DownloadFile(string url, string path) {
            using (UnityWebRequest www = UnityWebRequest.Get(url)) {
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError) {
                    Plugin.Log(www.error);
                }
                else {
                    File.WriteAllBytes(path, www.downloadHandler.data);
                }
            }
        }

        IEnumerator DownloadSongs(string author, string authorID) {
            var startTime = DateTime.Now;
            int downloadCount = 0;
            int totalSongs = 0;
            int currentSongIndex = 0;

            if (!Directory.Exists(".songcache")) {
                Directory.CreateDirectory(".songcache");
            }
            
            if (!Directory.Exists("CustomSongs")) {
                Directory.CreateDirectory("CustomSongs");
            }

            while (true) {
                string url = $"https://beatsaver.com/browse/byuser/{authorID}/{currentSongIndex.ToString()}";
                using (UnityWebRequest www = UnityWebRequest.Get(url)) {
                    yield return www.SendWebRequest();
                    if (www.isNetworkError || www.isHttpError) {
                        Plugin.Log(www.error);
                    }
                    else {
                        string searchResult = www.downloadHandler.text;

                        string search = "/browse/detail/";
                        List<string> songIndices = new List<string>();
                        while (true) {
                            int index = searchResult.IndexOf(search);
                            if (index != -1) {
                                searchResult = searchResult.Substring(index + search.Length);
                                string songIndex = searchResult.Substring(0, searchResult.IndexOf('"'));
                                if (!songIndices.Contains(songIndex)) {
                                    songIndices.Add(songIndex);
                                }
                            }
                            else {
                                break;
                            }
                        }

                        if (songIndices.Count == 0) {
                            break;
                        }
                        
                        foreach (string songIndex in songIndices) {
                            string currentSongDirectory = $"{Environment.CurrentDirectory}\\CustomSongs\\{songIndex}";

                            if (!_songDownloadHistory.Contains(songIndex) && !Directory.Exists(currentSongDirectory)) {
                                Empty(new DirectoryInfo(".songcache"));

                                string localPath = $"{Environment.CurrentDirectory}\\.songcache\\{songIndex}.zip";
                                yield return DownloadFile($"https://beatsaver.com/download/{songIndex}", localPath);

                                if (File.Exists(localPath)) {
                                    bool extracted = false;
                                    try {
                                        ZipFile.ExtractToDirectory(localPath, ".songcache");
                                        extracted = true;
                                    }
                                    catch (Exception) { }

                                    yield return null;

                                    File.Delete(localPath);

                                    if (extracted) {
                                        string[] directories = Directory.GetDirectories($"{Environment.CurrentDirectory}\\.songcache");
                                        foreach (var directory in directories) {
                                            Directory.Move(directory, currentSongDirectory);
                                        }
                                        downloadCount++;
                                    }
                                }
                            }
                            
                            // Keep a history of all the songs we download- count it as downloaded even if the user already had it downloaded previously so if they delete it it doesn't get redownloaded
                            if (!_songDownloadHistory.Contains(songIndex)) {
                                _songDownloadHistory.Add(songIndex);
                            }

                            // Check for/remove any duplicate songs
                            string[] customSongDirectories = Directory.GetDirectories($"{Environment.CurrentDirectory}\\CustomSongs");
                            string id = songIndex.Substring(0, songIndex.IndexOf("-"));
                            string version = songIndex.Substring(songIndex.IndexOf("-")+1);

                            foreach (string directory in customSongDirectories) {
                                string directoryName = directory.Substring(directory.LastIndexOf('\\') + 1);
                                bool hasDash = directoryName.Contains("-");


                                if (hasDash && directoryName.StartsWith($"{id}-") || !hasDash && directoryName == id) {
                                    if (directoryName != $"{id}-{version}") {
                                        Empty(new DirectoryInfo(directory));
                                        Directory.Delete(directory);
                                        Plugin.Log($"Deleting duplicate song with identifier \"{directoryName}\" (current version: {id}-{version})");
                                    }
                                }
                            }


                            totalSongs++;
                        }
                        currentSongIndex += 20;
                    }
                }
            }
            Empty(new DirectoryInfo(".songcache"));
            Directory.Delete(".songcache");

            Plugin.Log($"Downloaded {downloadCount.ToString()} songs from {author} in {((DateTime.Now - startTime).Seconds.ToString())} seconds. Skipped {(totalSongs-downloadCount).ToString()} songs.");
        }
    }
}
