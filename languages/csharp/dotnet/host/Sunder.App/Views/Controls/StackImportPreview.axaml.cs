using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Sunder.App.Views.Controls;

public partial class StackImportPreview : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ActionsProperty = AvaloniaProperty.Register<StackImportPreview, IEnumerable?>(nameof(Actions));
    public static readonly StyledProperty<IDataTemplate?> ActionTemplateProperty = AvaloniaProperty.Register<StackImportPreview, IDataTemplate?>(nameof(ActionTemplate));
    public static readonly StyledProperty<IEnumerable?> RequiredInputsProperty = AvaloniaProperty.Register<StackImportPreview, IEnumerable?>(nameof(RequiredInputs));
    public static readonly StyledProperty<IDataTemplate?> RequiredInputTemplateProperty = AvaloniaProperty.Register<StackImportPreview, IDataTemplate?>(nameof(RequiredInputTemplate));
    public static readonly StyledProperty<IEnumerable?> WarningsProperty = AvaloniaProperty.Register<StackImportPreview, IEnumerable?>(nameof(Warnings));
    public static readonly StyledProperty<IEnumerable?> ErrorsProperty = AvaloniaProperty.Register<StackImportPreview, IEnumerable?>(nameof(Errors));
    public static readonly StyledProperty<bool> HasActionsProperty = AvaloniaProperty.Register<StackImportPreview, bool>(nameof(HasActions));
    public static readonly StyledProperty<bool> HasRequiredInputsProperty = AvaloniaProperty.Register<StackImportPreview, bool>(nameof(HasRequiredInputs));
    public static readonly StyledProperty<bool> HasWarningsProperty = AvaloniaProperty.Register<StackImportPreview, bool>(nameof(HasWarnings));
    public static readonly StyledProperty<bool> HasErrorsProperty = AvaloniaProperty.Register<StackImportPreview, bool>(nameof(HasErrors));

    public StackImportPreview() => InitializeComponent();

    public IEnumerable? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public IDataTemplate? ActionTemplate { get => GetValue(ActionTemplateProperty); set => SetValue(ActionTemplateProperty, value); }
    public IEnumerable? RequiredInputs { get => GetValue(RequiredInputsProperty); set => SetValue(RequiredInputsProperty, value); }
    public IDataTemplate? RequiredInputTemplate { get => GetValue(RequiredInputTemplateProperty); set => SetValue(RequiredInputTemplateProperty, value); }
    public IEnumerable? Warnings { get => GetValue(WarningsProperty); set => SetValue(WarningsProperty, value); }
    public IEnumerable? Errors { get => GetValue(ErrorsProperty); set => SetValue(ErrorsProperty, value); }
    public bool HasActions { get => GetValue(HasActionsProperty); set => SetValue(HasActionsProperty, value); }
    public bool HasRequiredInputs { get => GetValue(HasRequiredInputsProperty); set => SetValue(HasRequiredInputsProperty, value); }
    public bool HasWarnings { get => GetValue(HasWarningsProperty); set => SetValue(HasWarningsProperty, value); }
    public bool HasErrors { get => GetValue(HasErrorsProperty); set => SetValue(HasErrorsProperty, value); }
}
