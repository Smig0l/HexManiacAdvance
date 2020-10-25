﻿using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Factory;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class SpriteElementViewModel : PagedElementViewModel, IPixelViewModel {
      private string runFormat;
      private SpriteFormat format;

      public short[] PixelData { get; private set; }
      public int PixelWidth => format.TileWidth * 8;
      public int PixelHeight => format.TileHeight * 8;
      public double SpriteScale { get; private set; }

      public bool HasMultiplePalettes => MaxPalette > 0;
      private int currentPalette;
      public int CurrentPalette { get => currentPalette; set => Set(ref currentPalette, value, arg => {
         UpdateTiles(CurrentPage);
         foreach (var child in ViewPort.Tools.TableTool.Children) {
            if (child is SpriteElementViewModel sevm && sevm != this && sevm.MaxPalette == MaxPalette) sevm.CurrentPalette = CurrentPalette;
         }
         UpdatePaletteSelection();
      }); }
      public int MaxPalette { get; private set; }

      public ISpriteRun GetRun(int start = int.MinValue) {
         if (start == int.MinValue) start = Start;
         var destination = ViewPort.Model.ReadPointer(start);
         var run = ViewPort.Model.GetNextRun(destination) as ISpriteRun;
         if (run == null) {
            IFormattedRun tempRun = new NoInfoRun(destination, new SortedSpan<int>(Start));
            var errorCode = FormatRunFactory.GetStrategy(runFormat).TryParseData(Model, string.Empty, destination, ref tempRun);
            run = tempRun as ISpriteRun;
         }
         return run;
      }

      #region OpenEditor Command

      private StubCommand openEditor;
      public ICommand OpenEditor => StubCommand(ref openEditor, ExecuteOpenEditor, CanExecuteImageEditor);

      private bool CanExecuteImageEditor() {
         var run = GetRun();
         return run?.SupportsEdit ?? false;
      }

      private void ExecuteOpenEditor() {
         var destination = ViewPort.Model.ReadPointer(Start);
         ViewPort.OpenImageEditorTab(destination, CurrentPage, CurrentPalette);
      }

      #endregion

      public override bool ShowPageControls => base.ShowPageControls || CanExecuteImageEditor();

      public SpriteElementViewModel(ViewPort viewPort, string runFormat, SpriteFormat format, int itemAddress) : base(viewPort, itemAddress) {
         this.format = format;
         this.runFormat = runFormat;
         var destination = ViewPort.Model.ReadPointer(Start);
         var run = GetRun();
         Pages = run.Pages;
         UpdateAvailablePalettes(Start);
      }

      private void UpdateAvailablePalettes(int start) {
         var run = GetRun(start);
         PaletteSelection.Clear();
         var index = 0;
         foreach (var palette in run.FindRelatedPalettes(ViewPort.Model)) {
            var name = ViewPort.BuildElementName(ViewPort.Model, palette.Start);
            var ps = new SelectionViewModel { Name = name, Selected = PaletteSelection.Count == currentPalette, Index = index };
            ps.Bind(nameof(ps.Selected), (o, e) => { if (o.Selected) CurrentPalette = o.Index; });
            PaletteSelection.Add(ps);
            index += 1;
         }
         MaxPalette = PaletteSelection.Count - 1;
      }

      public ObservableCollection<SelectionViewModel> PaletteSelection { get; } = new ObservableCollection<SelectionViewModel>();
      private void UpdatePaletteSelection() {
         for (int i = 0; i <= MaxPalette; i++) PaletteSelection[i].Selected = i == currentPalette;
      }

      /// <summary>
      /// Note that this method runs _before_ changes are copied from the baseclass
      /// So if we want to update tiles based on the new start point,
      /// Then UpdateColors can't rely on our internal start point
      /// </summary>
      protected override bool TryCopy(PagedElementViewModel other) {
         if (!(other is SpriteElementViewModel that)) return false;
         format = that.format;
         runFormat = that.runFormat;
         UpdateAvailablePalettes(that.Start);
         return true;
      }

      protected override void PageChanged() => UpdateTiles(CurrentPage);

      public void UpdateTiles(int? pageOption = null) {
         // TODO support multiple layers
         int page = pageOption ?? CurrentPage;

         if (GetRun() is LzTilemapRun mapRun) mapRun.FindMatchingTileset(Model);

         UpdateTiles(Start, page, false);
      }

      protected override bool CanExecuteAddPage() {
         var destination = ViewPort.Model.ReadPointer(Start);
         var run = GetRun();
         var canExecute = run is LzSpriteRun && CurrentPage == run.Pages - 1 && run.FindRelatedPalettes(Model).All(pal => pal.Pages == run.Pages && pal is LzPaletteRun);
         return canExecute;
      }

      protected override bool CanExecuteDeletePage() {
         var destination = ViewPort.Model.ReadPointer(Start);
         var run = GetRun();
         return run is LzSpriteRun && Pages > 1 && run.FindRelatedPalettes(Model).All(pal => pal.Pages == run.Pages && pal is LzPaletteRun);
      }

      private int[,] lastPixels;
      private IReadOnlyList<short> lastColors;
      private void UpdateTiles(int start, int page, bool exitPaletteSearchEarly) {
         var run = GetRun();
         var pixels = run.GetPixels(ViewPort.Model, page);
         var palette = GetDesiredPalette(start, page, exitPaletteSearchEarly, out var paletteFormat);
         if (pixels == lastPixels && palette == lastColors) return;
         lastPixels = pixels;
         lastColors = palette;
         if (!(run is LzTilemapRun)) paletteFormat = default;

         PixelData = SpriteTool.Render(pixels, palette, paletteFormat.InitialBlankPages, CurrentPage);
         
         NotifyPropertyChanged(nameof(PixelWidth));
         NotifyPropertyChanged(nameof(PixelHeight));
         NotifyPropertyChanged(nameof(PixelData));
         if (SpriteTool.MaxSpriteWidth < PixelWidth) {
            SpriteScale = .5;
         } else if (SpriteTool.MaxSpriteWidth > PixelWidth * 2) {
            SpriteScale = 2;
         } else {
            SpriteScale = 1;
         }
         NotifyPropertyChanged(nameof(SpriteScale));
      }

      /// <summary>
      /// If the hint is a table name, only match palettes from that table.
      /// </summary>
      private IReadOnlyList<short> GetDesiredPalette(int start, int page, bool exitEarly, out PaletteFormat paletteFormat) {
         paletteFormat = default;

         if (GetRun() is ISpriteRun sRun) {
            var palettes = sRun.FindRelatedPalettes(Model, Start, format.PaletteHint).ToList();
            var palette = palettes.FirstOrDefault();
            if (palettes.Count > 1 && palettes.Count > CurrentPalette) palette = palettes[CurrentPalette];
            if (palette != null) {
               paletteFormat = palette.PaletteFormat;
               return palette.AllColors(Model);
            }
            if (sRun.SpriteFormat.BitsPerPixel == 1) return TileViewModel.CreateDefaultPalette(2);
         }
         return TileViewModel.CreateDefaultPalette(0x10);
      }

      /// <summary>
      /// If the hint-name matches the name of a table of a combobox also loaded as a parallel table,
      /// then there isn't a 1-to-1 mapping between sprites and palettes.
      /// Instead, each sprite has an index (from the combobox) that matches a palette.
      /// In this case, the combobox will know which list of palettes it's pulling from.
      /// </summary>
      private bool TryGetPaletteFromComboBoxInMatchingTable(int start, ComboBoxArrayElementViewModel comboBox, int page, out IReadOnlyList<short> colors) {
         colors = null;

         // figure out the current index into the table
         var myRun = (ITableRun)Model.GetNextRun(start);
         var arrayIndex = (start - myRun.Start) / myRun.ElementLength;

         // figure out the name of the palette table
         var indexRun = (ITableRun)Model.GetNextRun(comboBox.Start);
         var offsets = indexRun.ConvertByteOffsetToArrayOffset(comboBox.Start);
         if (offsets.SegmentOffset != 0) return false; // for now, require that 
         if (!(indexRun.ElementContent[offsets.SegmentIndex] is ArrayRunEnumSegment segment)) return false;
         var paletteTable = segment.EnumName;

         // figure out which element into the palette run from the value in the index run
         var paletteIndex = Model.ReadMultiByteValue(indexRun.Start + indexRun.ElementLength * arrayIndex, segment.Length);

         // find the pointer to our palette
         if (!(Model.GetNextRun(Model.GetAddressFromAnchor(ViewPort.CurrentChange, -1, paletteTable)) is ArrayRun array)) return false;
         if (array.ElementContent[0].Type != ElementContentType.Pointer) return false;
         var pointer = array.Start + array.ElementLength * paletteIndex;

         // go get the palette that we want
         var paletteAddress = Model.ReadPointer(pointer);
         if (!(Model.GetNextRun(paletteAddress) is IPaletteRun palRun)) return false;

         // return the values from the palette
         colors = palRun.GetPalette(Model, page);
         return true;
      }
   }

   public class TileViewModel : ViewModelCore {
      public byte[] DataStore { get; }
      public int Start { get; }

      /// <summary>
      /// Encoded as 5,6,5 bits for r,g,b
      /// </summary>
      public IReadOnlyList<short> Palette { get; private set; }

      public TileViewModel(byte[] data, int start, int byteLength, IReadOnlyList<short> palette) {
         DataStore = data;
         Start = start;
         Palette = palette;

         if (palette != null) return;

         int desiredCount = (int)Math.Pow(2, byteLength / 8);
         Palette = CreateDefaultPalette(desiredCount);
      }

      // TODO include horizontal/vertical flip information

      public static short[] CreateDefaultPalette(int desiredCount) {
         var palette = new short[desiredCount];
         for (int i = 0; i < desiredCount; i++) {
            var shade = 0b11111 * i / (desiredCount - 1);
            var color = (shade << 10) | (shade << 5) | shade;
            palette[i] = (short)color;
         }
         return palette;
      }
   }
}
