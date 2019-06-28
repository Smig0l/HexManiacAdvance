﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Text;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class PCSTool : ViewModelCore, IToolViewModel {
      private readonly IDataModel model;
      private readonly Selection selection;
      private readonly ChangeHistory<ModelDelta> history;
      private readonly IToolTrayViewModel runner;
      private readonly StubCommand
         checkIsText = new StubCommand(),
         insertText = new StubCommand();

      // if we're in the middle of updating ourselves, we may notify changes to other controls.
      // while we do, ignore any updates coming from those controls, since we may be in an inconsistent state.
      private bool ignoreExternalUpdates = false;

      public string Name => "String";

      private int contentIndex;
      public int ContentIndex {
         get => contentIndex;
         set {
            if (TryUpdate(ref contentIndex, value)) UpdateSelectionFromTool();
         }
      }

      private int contentSelectionLength;
      public int ContentSelectionLength {
         get => contentSelectionLength;
         set {
            if (TryUpdate(ref contentSelectionLength, value)) UpdateSelectionFromTool();
         }
      }

      public ICommand CheckIsText => checkIsText;
      public ICommand InsertText => insertText;

      private bool showMessage;
      public bool ShowMessage {
         get => showMessage;
         set {
            if (TryUpdate(ref showMessage, value)) {
               checkIsText.CanExecuteChanged?.Invoke(checkIsText, EventArgs.Empty);
               insertText.CanExecuteChanged?.Invoke(checkIsText, EventArgs.Empty);
            }
         }
      }

      private string message;
      public string Message {
         get => message;
         set => TryUpdate(ref message, value);
      }

      private string content;
      public string Content {
         get => content;
         set {
            if (TryUpdate(ref content, value)) {
               var run = model.GetNextRun(address);
               if (run.Start > address) return; // wrong run, don't adjust
               if (run is IStreamRun streamRun) UpdateRun(streamRun);
               if (run is ArrayRun arrayRun) UpdateRun(arrayRun);
            }
         }
      }

      private int address = Pointer.NULL;
      public int Address {
         get => address;
         set {
            if (ignoreExternalUpdates) return;
            var run = model.GetNextRun(value);
            if (TryUpdate(ref address, value)) {
               if ((run is IStreamRun || run is ArrayRun) && run.Start <= value) {
                  runner.Schedule(DataForCurrentRunChanged);
                  Enabled = true;
                  ShowMessage = false;
               } else {
                  Enabled = false;
                  ShowMessage = true;
                  Message = $"{address.ToString("X6")} does not appear to be text.";
               }
            }
         }
      }

      private bool enabled;
      public bool Enabled {
         get => enabled;
         private set {
            if (TryUpdate(ref enabled, value)) {
               if (!enabled) Content = string.Empty;
            }
         }
      }

      public event EventHandler<string> OnError;
      public event EventHandler<IFormattedRun> ModelDataChanged;
      public event EventHandler<(int originalLocation, int newLocation)> ModelDataMoved;

      public PCSTool(IDataModel model, Selection selection, ChangeHistory<ModelDelta> history, IToolTrayViewModel runner) {
         this.model = model;
         this.selection = selection;
         this.history = history;
         this.runner = runner;
         checkIsText.CanExecute = arg => ShowMessage;
         checkIsText.Execute = arg => {
            var startPlaces = model.FindPossibleTextStartingPlaces(Address, 1);
            var results = model.ConsiderResultsAsTextRuns(history.CurrentChange, startPlaces);
            if (results == 0) {
               OnError?.Invoke(this, $"Could not discover text at {address.ToString("X6")}.");
            } else {
               runner.Schedule(DataForCurrentRunChanged);
               Enabled = true;
               ShowMessage = false;
               ModelDataChanged(this, model.GetNextRun(address));
            }
         };
         insertText.CanExecute = arg => ShowMessage;
         insertText.Execute = arg => {
            if (address < 0 || model.Count <= address || model[address] != 0xFF || model.GetNextRun(address).Start <= address) {
               OnError?.Invoke(this, $"Could not insert text at {address.ToString("X6")}.{Environment.NewLine}The bytes must be usused (FF).");
               return;
            }
            var gameCode = PokemonModel.ReadGameCode(model);
            var initialAddress = address.ToString("X6");
            var newRun = new PCSRun(model, address, 1, null);
            history.CurrentChange.AddRun(newRun);
            model.ObserveAnchorWritten(history.CurrentChange, gameCode + initialAddress, newRun);
            ModelDataChanged?.Invoke(this, newRun);
            runner.Schedule(DataForCurrentRunChanged);
            Enabled = true;
            ShowMessage = false;
         };
      }

      public void DataForCurrentRunChanged() {
         var run = model.GetNextRun(address);
         if (run is ArrayRun array) {
            var offsets = array.ConvertByteOffsetToArrayOffset(address);
            var segment = array.ElementContent[offsets.SegmentIndex];
            if (segment.Type == ElementContentType.PCS) {
               var lines = new string[array.ElementCount];
               var textStart = offsets.SegmentStart - offsets.ElementIndex * array.ElementLength; // the starting address of the first text element
               for (int i = 0; i < lines.Length; i++) {
                  var newContent = PCSString.Convert(model, textStart + i * array.ElementLength, segment.Length)?.Trim() ?? string.Empty;
                  newContent = RemoveQuotes(newContent);
                  lines[i] = newContent;
               }
               if (lines.Length > 0) {
                  var builder = new StringBuilder();
                  for (int i = 0; i < lines.Length; i++) {
                     builder.Append(lines[i]);
                     if (i != lines.Length - 1) builder.Append(Environment.NewLine);
                  }

                  // guard to prevent selection updates due to data changes from other tools/the main view
                  ignoreSelectionUpdates = true;
                  using (new StubDisposable { Dispose = () => ignoreSelectionUpdates = false }) {
                     TryUpdate(ref content, builder.ToString(), nameof(Content));
                  }
               }
            }
            return;
         } else if (run is IStreamRun stream) {
            using (ModelCacheScope.CreateScope(model)) {
               var newContent = stream.SerializeRun();
               ignoreSelectionUpdates = true;
               using (new StubDisposable { Dispose = () => ignoreSelectionUpdates = false }) {
                  TryUpdate(ref content, newContent, nameof(Content));
               }
            }
            return;
         }

         throw new NotImplementedException();
      }

      private string RemoveQuotes(string newContent) {
         if (newContent.Length == 0) return newContent;
         newContent = newContent.Substring(1);
         if (newContent.EndsWith("\"")) newContent = newContent.Substring(0, newContent.Length - 1);
         return newContent;
      }

      /// <summary>
      /// If a selection update is requested due to a change in the Content, ignore it.
      /// Otherwise, the selection update could send us back to the begining of the run.
      /// </summary>
      private bool ignoreSelectionUpdates;
      private void UpdateSelectionFromTool() {
         if (ignoreSelectionUpdates) return;
         var run = model.GetNextRun(Address);
         if (!(run is ArrayRun) && !(run is PCSRun) && !(run is EggMoveRun) && !(run is PLMRun)) return;

         // for arrays, the address must be at the start of a string segment within the first element of the array
         if (run is ArrayRun arrayRun) {
            var offset = arrayRun.ConvertByteOffsetToArrayOffset(Address);
            if (arrayRun.ElementContent[offset.SegmentIndex].Type != ElementContentType.PCS) return;  // must be a string
         }

         var content = Content;
         if (content.Length < contentIndex + contentSelectionLength) return; // transient invalid state
         var selectionStart = contentIndex;
         var selectionLength = contentSelectionLength;

         if (run is PCSRun) {
            selectionLength = Math.Max(PCSString.Convert(content.Substring(selectionStart, selectionLength)).Count - 2, 0); // remove 1 byte since 0xFF was added on and 1 byte since the selection should visually match
            selectionStart = PCSString.Convert(content.Substring(0, selectionStart)).Count - 1 + run.Start; // remove 1 byte since the 0xFF was added on
         } else if (run is ArrayRun array) {
            var offset = array.ConvertByteOffsetToArrayOffset(Address);
            var textStart = offset.SegmentStart - offset.ElementIndex * array.ElementLength; // the starting address of the first text element
            var leadingLines = content.Substring(0, contentIndex).Split(Environment.NewLine);
            selectionStart = textStart + (leadingLines.Length - 1) * array.ElementLength + leadingLines[leadingLines.Length - 1].Length;

            selectionLength = Math.Max(0, selectionLength - 1); // decrease by one since a selection of 0 and selection of 1 have no difference
            var afterLines = content.Substring(0, contentIndex + selectionLength).Split(Environment.NewLine);
            var selectionEnd = textStart + (afterLines.Length - 1) * array.ElementLength + afterLines[afterLines.Length - 1].Length;
            selectionLength = selectionEnd - selectionStart;
         } else if (run is EggMoveRun || run is PLMRun) { // both of these streams format by putting 2 bytes per line.
            var beforeSelection = content.Substring(0, selectionStart);
            var beforeLineCount = (beforeSelection.Split(Environment.NewLine).Length - 1).LimitToRange(0, int.MaxValue);
            var withSelection = content.Substring(0, selectionStart + selectionLength);
            var withSelectionLineCount = (withSelection.Split(Environment.NewLine).Length - 1).LimitToRange(0, int.MaxValue);

            selectionStart = run.Start + beforeLineCount * 2;
            var selectionEnd = run.Start + withSelectionLineCount * 2;
            selectionLength = selectionEnd - selectionStart;
         }

         selection.SelectionStart = selection.Scroll.DataIndexToViewPoint(selectionStart);
         selection.SelectionEnd = selection.Scroll.DataIndexToViewPoint(selectionStart + selectionLength);
      }

      private void UpdateRun(ArrayRun arrayRun) {
         var lines = content.Split(Environment.NewLine);
         var arrayByteLength = lines.Length * arrayRun.ElementLength;
         var newRun = (ArrayRun)model.RelocateForExpansion(history.CurrentChange, arrayRun, arrayByteLength);

         using (ModelCacheScope.CreateScope(model)) {
            TryUpdate(ref address, address + newRun.Start - arrayRun.Start, nameof(Address));

            var offsets = newRun.ConvertByteOffsetToArrayOffset(address);
            if (arrayRun.Start != newRun.Start) ModelDataMoved?.Invoke(this, (arrayRun.Start, newRun.Start));
            var segmentLength = newRun.ElementContent[offsets.SegmentIndex].Length;
            for (int i = 0; i < lines.Length; i++) {
               var bytes = PCSString.Convert(lines[i]);
               if (bytes.Count > segmentLength) bytes[segmentLength - 1] = 0xFF; // truncate and always end with an endstring character
               for (int j = 0; j < segmentLength; j++) {
                  if (j < bytes.Count) {
                     history.CurrentChange.ChangeData(model, offsets.SegmentStart + i * newRun.ElementLength + j, bytes[j]);
                  } else {
                     history.CurrentChange.ChangeData(model, offsets.SegmentStart + i * newRun.ElementLength + j, 0x00);
                  }
               }
            }

            if (newRun.ElementCount != lines.Length) {
               newRun = newRun.Append(lines.Length - newRun.ElementCount);
               model.ObserveRunWritten(history.CurrentChange, newRun);
               history.CurrentChange.AddRun(newRun);
            }

            ModelDataChanged?.Invoke(this, newRun);
         }
      }

      private void UpdateRun(IStreamRun run) {
         ignoreExternalUpdates = true;
         using (ModelCacheScope.CreateScope(model)) {
            var newRun = run.DeserializeRun(content, history.CurrentChange);
            if (newRun.Length != run.Length) {
               model.ObserveRunWritten(history.CurrentChange, newRun);
               newRun = (IStreamRun)model.GetNextRun(newRun.Start);
               history.CurrentChange.AddRun(newRun);
               if (newRun is EggMoveRun eggRun) eggRun.UpdateLimiter(history.CurrentChange);
            }

            if (run.Start != newRun.Start) ModelDataMoved?.Invoke(this, (run.Start, newRun.Start));
            ModelDataChanged?.Invoke(this, newRun);
            TryUpdate(ref address, newRun.Start, nameof(Address));
            ignoreExternalUpdates = false;
         }
      }
   }
}
