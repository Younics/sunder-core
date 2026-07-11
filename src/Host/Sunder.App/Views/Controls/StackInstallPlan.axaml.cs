using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Sunder.App.Views.Controls;

public partial class StackInstallPlan : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty = AvaloniaProperty.Register<StackInstallPlan, IEnumerable?>(nameof(Items));
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty = AvaloniaProperty.Register<StackInstallPlan, IDataTemplate?>(nameof(ItemTemplate));
    public static readonly StyledProperty<IEnumerable?> WarningsProperty = AvaloniaProperty.Register<StackInstallPlan, IEnumerable?>(nameof(Warnings));
    public static readonly StyledProperty<IEnumerable?> ErrorsProperty = AvaloniaProperty.Register<StackInstallPlan, IEnumerable?>(nameof(Errors));
    public static readonly StyledProperty<bool> HasItemsProperty = AvaloniaProperty.Register<StackInstallPlan, bool>(nameof(HasItems));
    public static readonly StyledProperty<bool> HasWarningsProperty = AvaloniaProperty.Register<StackInstallPlan, bool>(nameof(HasWarnings));
    public static readonly StyledProperty<bool> HasErrorsProperty = AvaloniaProperty.Register<StackInstallPlan, bool>(nameof(HasErrors));
    public static readonly StyledProperty<bool> ShowNoChangesProperty = AvaloniaProperty.Register<StackInstallPlan, bool>(nameof(ShowNoChanges));

    public StackInstallPlan() => InitializeComponent();

    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public IEnumerable? Warnings { get => GetValue(WarningsProperty); set => SetValue(WarningsProperty, value); }
    public IEnumerable? Errors { get => GetValue(ErrorsProperty); set => SetValue(ErrorsProperty, value); }
    public bool HasItems { get => GetValue(HasItemsProperty); set => SetValue(HasItemsProperty, value); }
    public bool HasWarnings { get => GetValue(HasWarningsProperty); set => SetValue(HasWarningsProperty, value); }
    public bool HasErrors { get => GetValue(HasErrorsProperty); set => SetValue(HasErrorsProperty, value); }
    public bool ShowNoChanges { get => GetValue(ShowNoChangesProperty); set => SetValue(ShowNoChangesProperty, value); }
}
