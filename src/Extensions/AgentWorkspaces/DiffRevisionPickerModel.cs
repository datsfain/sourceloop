using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Extensions.AgentWorkspaces
{
    // The model behind the revision picker flyout, shaped like ViewModels.CompareCommandPalette.
    public class DiffRevisionPickerModel : ObservableObject
    {
        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                    Update();
            }
        }

        public List<DiffRevision> Options
        {
            get => _options;
            private set => SetProperty(ref _options, value);
        }

        public DiffRevision Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }

        public DiffRevisionPickerModel(DiffPage page, Action<DiffRevision> picked)
        {
            _page = page;
            _picked = picked;
            Update();
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
        }

        public void Confirm()
        {
            if (_selected != null)
                _picked(_selected);
        }

        private void Update()
        {
            var options = _page.GetOptions(_filter);
            var keep = _selected != null && options.Contains(_selected) ? _selected : null;
            Options = options;
            Selected = keep ?? (options.Count > 0 ? options[0] : null);
        }

        private readonly DiffPage _page;
        private readonly Action<DiffRevision> _picked;
        private string _filter = string.Empty;
        private List<DiffRevision> _options = [];
        private DiffRevision _selected;
    }
}
