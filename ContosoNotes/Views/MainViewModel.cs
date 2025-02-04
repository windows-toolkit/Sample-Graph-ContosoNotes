﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using CommunityToolkit.Authentication;
using CommunityToolkit.Graph.Extensions;
using ContosoNotes.Models;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Windows.System;

namespace ContosoNotes.Views
{
    public class MainViewModel : ObservableObject
    {
        private static readonly SemaphoreSlim _mutex = new (1);

        public RelayCommand CreateNotePageCommand { get; }
        public RelayCommand DeleteCurrentNotePageCommand { get; }
        public RelayCommand<TaskNoteItemModel> DeleteTaskCommand { get; }
        public RelayCommand LaunchMicrosoftTodoCommand { get; }
        public RelayCommand TogglePaneCommand { get; }
        public RelayCommand SaveCommand { get; }

        private bool _isPaneOpen;
        public bool IsPaneOpen
        {
            get => _isPaneOpen;
            set => SetProperty(ref _isPaneOpen, value);
        }

        private bool _isSignedIn;
        public bool IsSignedIn
        {
            get => _isSignedIn;
            set => SetProperty(ref _isSignedIn, value);
        }

        private NotesListModel _notesList;
        public NotesListModel NotesList
        {
            get => _notesList;
            set => SetProperty(ref _notesList, value);
        }

        private int _currentNotesListItemIndex;
        public int CurrentNotesListItemIndex
        {
            get => _currentNotesListItemIndex;
            set => SetProperty(ref _currentNotesListItemIndex, value);
        }

        private NotePageModel _currentNotePage;
        public NotePageModel CurrentNotePage
        {
            get => _currentNotePage;
            set => SetProperty(ref _currentNotePage, value);
        }

        private DateTime? _lastSync;
        public DateTime? LastSync
        {
            get => _lastSync;
            set => SetProperty(ref _lastSync, value);
        }

        private readonly StorageManager _storageManager;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherQueueTimer _timer;

        public MainViewModel()
        {
            LaunchMicrosoftTodoCommand = new(LaunchMicrosoftTodo);
            CreateNotePageCommand = new(CreateNewNotePage);
            DeleteCurrentNotePageCommand = new(DeleteCurrentNotePage);
            TogglePaneCommand = new(TogglePane);
            SaveCommand = new(Save);
            DeleteTaskCommand = new(DeleteTask);

            _currentNotePage = null;
            _currentNotesListItemIndex = -1;
            _isSignedIn = ProviderManager.Instance.GlobalProvider?.State == ProviderState.SignedIn;
            _isPaneOpen = false;
            _lastSync = null;
            _notesList = null;
            _storageManager = new StorageManager();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _timer = _dispatcherQueue.CreateTimer();

            KeywordDetector.Instance.RegisterKeyword("todo:");
            KeywordDetector.Instance.KeywordDetected += OnKeywordDetected;

            PropertyChanged += OnPropertyChanged;

            InitializeGlobalProvider();
        }

        /// <summary>
        /// Initialize the global authentication provider.
        /// </summary>
        private void InitializeGlobalProvider()
        {
            ProviderManager.Instance.ProviderStateChanged += OnProviderStateChanged;

            string[] scopes = new string[] { "User.Read", "Tasks.ReadWrite", "Files.ReadWrite" };
            ProviderManager.Instance.GlobalProvider = new WindowsProvider(scopes);
        }

        private async void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LastSync))
            {
                return;
            }

            if (e.PropertyName == nameof(CurrentNotesListItemIndex) && _currentNotesListItemIndex != -1)
            {
                var currentNotesListItem = _notesList.Items[_currentNotesListItemIndex];
                if (currentNotesListItem.NotePageId != _currentNotePage?.Id)
                {
                    try
                    {
                        CurrentNotePage = await _storageManager.GetNotePageAsync(currentNotesListItem);
                    }
                    catch
                    {
                        // Todo: Handle failure to load a note page from file
                    }
                }
            }
            else if (e.PropertyName == nameof(CurrentNotePage) && _notesList.Items.Count > 0)
            {
                if (_currentNotePage != null)
                {
                    if (_currentNotesListItemIndex > -1)
                    {
                        var currentNotesListItem = _notesList.Items[_currentNotesListItemIndex];
                        if (_currentNotePage.Id == currentNotesListItem?.NotePageId)
                        {
                            return;
                        }
                    }

                    for (var i = 0; i < _notesList.Items.Count; i++)
                    {
                        var notesListItem = _notesList.Items[i];
                        if (_currentNotePage.Id == notesListItem.NotePageId)
                        {
                            CurrentNotesListItemIndex = i;
                            break;
                        }
                    }
                }
            }
        }

        private void DeleteCurrentNotePage()
        {
            // We don't actually delete the notes, but they do become orphaned by removing them from the notes list.
            NotesList.Items.RemoveAt(_currentNotesListItemIndex);

            if (NotesList.Items.Count == 0)
            {
                CreateNewNotePage();
            }

            CurrentNotesListItemIndex = Math.Max(0, _currentNotesListItemIndex - 1);

            Save();
        }

        private void CreateNotesList()
        {
            NotesList = new NotesListModel();
        }

        private void CreateNewNotePage()
        {
            // Create a new empty NotePageModel, with a fresh item ready for input
            var newNotePage = new NotePageModel()
            {
                PageTitle = "New note",
            };
            newNotePage.NoteItems.Add(new NoteItemModel());

            if (_notesList == null)
            {
                CreateNotesList();
            }

            // Update the NotesList
            NotesList.Items.Insert(0, new NotesListItemModel()
            {
                NotePageId = newNotePage.Id,
                NotePageTitle = newNotePage.PageTitle,
            });
            
            // Set the current page
            CurrentNotePage = newNotePage;
            CurrentNotesListItemIndex = 0;
        }

        private async void LaunchMicrosoftTodo()
        {
            string taskListId = string.Empty;

            var provider = ProviderManager.Instance.GlobalProvider;
            if (provider != null && provider.State == ProviderState.SignedIn)
            {
                var graphClient = ProviderManager.Instance.GlobalProvider.GetClient();

                try
                {
                    var existingLists = await graphClient.Me.Todo.Lists.Request().Filter("displayName eq 'ContosoNotes'").GetAsync();
                    if (existingLists.Count > 0)
                    {
                        taskListId = existingLists[0].Id;
                    }
                }
                catch
                {
                }
            }

            var uri = new Uri("https://to-do.live.com/tasks/" + taskListId);
            await Launcher.LaunchUriAsync(uri);
        }

        private void TogglePane()
        {
            IsPaneOpen = !IsPaneOpen;
        }

        private void OnProviderStateChanged(object sender, ProviderStateChangedEventArgs e)
        {
            IsSignedIn = ProviderManager.Instance.GlobalProvider?.State == ProviderState.SignedIn;

            if (ProviderManager.Instance.GlobalProvider?.State != ProviderState.Loading)
            {
                Load();
            }
        }


        public async void Load()
        {
            await _mutex.WaitAsync();

            // Handle any existing NotePage
            if (CurrentNotePage != null)
            {
                if (CurrentNotePage.IsEmpty)
                {
                    CurrentNotePage = null;
                }
                else
                {
                    try
                    {
                        // We have transitioned between local and roaming data, but there is already an active NotePage with content.
                        // Save the progress (overriding any existing) and continue to use it as the current NotePage.
                        await _storageManager.SaveCurrentNotePageAsync(_currentNotePage);
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                // Clear the notes list so we can repopulate it.
                NotesList = await _storageManager.GetNotesListAsync();
            }
            catch
            {
            }

            if (_notesList == null)
            {
                CreateNotesList();
            }

            // If we have notes in the list, attempt to pull the active/current note page.
            if (_notesList.Items.Count > 0)
            {
                if (_currentNotePage == null)
                {
                    try
                    {
                        CurrentNotePage = await _storageManager.GetCurrentNotePageAsync(_notesList);
                    }
                    catch
                    {
                    }
                }

                if (_currentNotePage == null)
                {
                    try
                    {
                        // We didn't find a "current" page, so just grab the first one in the list.
                        CurrentNotePage = await _storageManager.GetNotePageAsync(_notesList.Items[0]);
                    }
                    catch
                    {
                    }
                }

                if (_currentNotePage != null)
                {
                    foreach (var item in _notesList.Items)
                    {
                        if (item.NotePageId == _currentNotePage.Id)
                        {
                            CurrentNotesListItemIndex = _notesList.Items.IndexOf(item);
                            break;
                        }
                    }

                    Save();
                }
            }

            if (_currentNotePage == null)
            {
                CreateNewNotePage();
            }

            InitSaveTimer();
                
            _mutex.Release();
        }

        private void InitSaveTimer()
        {
            if (!_timer.IsRunning)
            {
                _timer.Tick += OnTimerTick;
                _timer.Interval = TimeSpan.FromSeconds(10);
                _timer.IsRepeating = true;

                _timer.Start();
            }
        }

        private void OnTimerTick(DispatcherQueueTimer timer, object e)
        {
            // TODO: Detect changes before saving. 

            Save();
        }

        private async void Save()
        {
            if (_currentNotePage == null || _currentNotePage.IsEmpty)
            {
                return;
            }

            if (_notesList == null)
            {
                
            }

            // Find and update the note page title in the notes list.
            foreach (var item in _notesList.Items)
            {
                if (item.NotePageId == _currentNotePage.Id)
                {
                    item.NotePageTitle = _currentNotePage.PageTitle;
                    break;
                }
            }

            try
            {
                // Save any existing NotePage
                await _storageManager.SaveCurrentNotePageAsync(_currentNotePage);

                // Update the NotesList
                await _storageManager.SaveNotesListAsync(_notesList);

                LastSync = DateTime.Now;
            }
            catch
            {
            }
        }

        private void OnKeywordDetected(object sender, KeywordDetectedEventArgs e)
        {
            var noteItem = sender as NoteItemModel;

            // Handle any registered keywords.
            if (e.Keyword == "todo:")
            {
                var noteItemIndex = CurrentNotePage.NoteItems.IndexOf(noteItem);

                if (string.IsNullOrEmpty(e.PreText))
                {
                    // Remove the now empty note item.
                    CurrentNotePage.NoteItems.RemoveAt(noteItemIndex--);
                }
                else
                {
                    // Update the existing NoteItem with the text prior to the detected keyword.
                    CurrentNotePage.NoteItems[noteItemIndex].Text = e.PreText;
                }

                var taskItem = new TaskNoteItemModel();
                CurrentNotePage.NoteItems.Insert(++noteItemIndex, taskItem);

                // Insert a new item with the text from after the detected keyword, if any.
                if (!string.IsNullOrEmpty(e.PostText))
                {
                    // Check if we have a text note next to pre-pend with our split
                    if (++noteItemIndex < CurrentNotePage.NoteItems.Count && CurrentNotePage.NoteItems[noteItemIndex] is not TaskNoteItemModel)
                    {
                        var note = CurrentNotePage.NoteItems[noteItemIndex];
                        note.Text = e.PostText + note.Text;
                    }
                    else
                    {
                        // Otherwise we insert a new text note
                        var postItem = new NoteItemModel(e.PostText);
                        CurrentNotePage.NoteItems.Insert(noteItemIndex, postItem);
                    }
                }
                else if (++noteItemIndex == CurrentNotePage.NoteItems.Count)
                {
                    // If we're at the end we also want a blank note to help for navigating
                    var postItem = new NoteItemModel(" ");
                    CurrentNotePage.NoteItems.Insert(noteItemIndex, postItem);
                }
            }
        }

        private void DeleteTask(TaskNoteItemModel task)
        {
            var taskIndex = CurrentNotePage.NoteItems.IndexOf(task);

            CurrentNotePage.NoteItems.RemoveAt(taskIndex);

            // Check if we see a text note before us and after to merge
            if (taskIndex > 0 && CurrentNotePage.NoteItems[taskIndex - 1] is not TaskNoteItemModel &&
                taskIndex < CurrentNotePage.NoteItems.Count && CurrentNotePage.NoteItems[taskIndex] is not TaskNoteItemModel)
            {
                // Merge two texts together
                CurrentNotePage.NoteItems[taskIndex - 1].Text += CurrentNotePage.NoteItems[taskIndex].Text;

                CurrentNotePage.NoteItems.RemoveAt(taskIndex);
            }

            // Delete the task in the Graph as well.
            task.Delete();
        }
    }
}
