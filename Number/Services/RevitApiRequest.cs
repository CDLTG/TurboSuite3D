#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Number.Models;
using TurboSuite.Number.ViewModels;

namespace TurboSuite.Number.Services
{
    public abstract class RevitApiRequest
    {
        public Action<object> OnComplete { get; set; }
    }

    public class WritePanelSettingsRequest : RevitApiRequest
    {
        public IList<PanelSettingsModel> PanelSettings { get; set; }
    }

    public class WriteDeviceSwitchIdsRequest : RevitApiRequest
    {
        public IList<NumberableRowViewModel> Rows { get; set; }
    }

    public class GetOrCreateScheduleViewRequest : RevitApiRequest
    {
        public ElementId PanelId { get; set; }
    }

    public class GetSlotLayoutRequest : RevitApiRequest
    {
        public PanelScheduleView ScheduleView { get; set; }
    }

    public class MoveCircuitRequest : RevitApiRequest
    {
        public PanelScheduleView ScheduleView { get; set; }
        public int FromRow { get; set; }
        public int FromCol { get; set; }
        public int ToRow { get; set; }
        public int ToCol { get; set; }
    }

    public class AssignSpareRequest : RevitApiRequest
    {
        public PanelScheduleView ScheduleView { get; set; }
        public List<(int Row, int Col)> Slots { get; set; }
    }

    public class AssignSpaceRequest : RevitApiRequest
    {
        public PanelScheduleView ScheduleView { get; set; }
        public List<(int Row, int Col)> Slots { get; set; }
    }

    public class RemoveSpareSpaceRequest : RevitApiRequest
    {
        public PanelScheduleView ScheduleView { get; set; }
        public List<(int Row, int Col, string SlotType)> Slots { get; set; }
    }

    public class SaveRoomOrderRequest : RevitApiRequest
    {
        public List<string> RoomOrder { get; set; }
    }

    public class SaveSidebarVisibleRequest : RevitApiRequest
    {
        public bool IsVisible { get; set; }
    }

    public class SavePrefixSuffixRequest : RevitApiRequest
    {
        public string Prefix { get; set; }
        public string Suffix { get; set; }
    }

    public class OpenScheduleViewRequest : RevitApiRequest
    {
        public PanelScheduleView ScheduleView { get; set; }
    }

    public class RefreshCircuitsRequest : RevitApiRequest
    {
    }
}
