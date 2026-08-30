namespace VPM.Models
{
    /// <summary>Primary Hub jobs. Visible as window-level views, not inspector modes.</summary>
    public enum HubWorkView
    {
        Browse,
        Updates,
        Missing
    }

    /// <summary>Local-library filter applied to the current Browse page results.</summary>
    public enum HubLibraryFilter
    {
        All,
        NotInLibrary,
        Updates,
        MissingDeps
    }
}
