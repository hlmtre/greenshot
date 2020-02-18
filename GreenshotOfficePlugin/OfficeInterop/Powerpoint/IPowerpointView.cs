﻿using Greenshot.Interop;
using GreenshotOfficePlugin.OfficeInterop.Word;

namespace GreenshotOfficePlugin.OfficeInterop.Powerpoint
{
    /// <summary>
    /// See: http://msdn.microsoft.com/en-us/library/microsoft.office.interop.powerpoint.view_members.aspx
    /// </summary>
    public interface IPowerpointView : ICommon {
        IZoom Zoom { get; }
        void GotoSlide(int index);
    }
}