using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace AiAssistant.UI.Controls;

public partial class WelcomeView : UserControl
{
    /// <summary>
    /// Raised when the user clicks the primary action button while an API key IS
    /// configured (normal "Let's Get Started" flow — focuses the chat input).
    /// </summary>
    public event EventHandler? GetStarted;

    /// <summary>
    /// Raised when the user clicks the primary action button while NO API key is
    /// configured for any provider. The button switches to "Configure API Key" and
    /// this event should route to opening Settings, so first-time users aren't sent
    /// straight to a failed first message instead.
    /// </summary>
    public event EventHandler? ConfigureApiKeyRequested;

    private bool _isConfigured = true;
    private System.Windows.Threading.DispatcherTimer? _carouselTimer;
    private Grid[] _featureItems = Array.Empty<Grid>();
    private RadioButton[] _indicators = Array.Empty<RadioButton>();
    private int _currentFeatureIndex = 0;

    /// <summary>
    /// Whether any provider currently has an API key configured. When false, the
    /// primary button switches from "Let's Get Started" to "Configure API Key".
    /// </summary>
    public bool IsConfigured
    {
        get => _isConfigured;
        set
        {
            _isConfigured = value;
            UpdateButtonForConfiguration();
        }
    }

    public WelcomeView()
    {
        Helpers.ThemeManager.Initialize();
        InitializeComponent();

        // FIX (startup hang): Only start animations and timers when the control is actually
        // made visible. When ChatPanel initialises, WelcomeView starts as Visibility=Collapsed;
        // firing DispatcherTimers and infinite BeginAnimation calls at that point wastes CPU
        // and contributes to the startup freeze.
        // • Loaded wires theme and subscribes to IsVisibleChanged for deferred init.
        // • IsVisibleChanged starts the animations the first time the panel is shown.
        this.Loaded += WelcomeView_Loaded;
        this.Unloaded += WelcomeView_Unloaded;
        this.IsVisibleChanged += WelcomeView_IsVisibleChanged;
    }

    private void WelcomeView_Unloaded(object sender, RoutedEventArgs e)
    {
        _carouselTimer?.Stop();
        Helpers.ThemeManager.ThemeChanged -= OnThemeChanged;
        
        // Remove infinite animations to free compositor resources
        ApexCodeLogo.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);

        // Reset so entrance animations replay if the control is re-loaded (e.g. new session).
        _animationsStarted = false;
    }

    private bool _animationsStarted = false;

    private void WelcomeView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && !_animationsStarted)
        {
            _animationsStarted = true;
            StartEntranceAndCarousel();
        }
    }

    private void WelcomeView_Loaded(object sender, RoutedEventArgs e)
    {
        Helpers.ThemeManager.ThemeChanged -= OnThemeChanged;
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged(null, EventArgs.Empty);

        // Only kick off animations immediately if the control is already visible
        // (e.g. re-parented into a live visual tree that is shown from the start).
        // The common case is Visibility=Collapsed on startup, so we defer via
        // IsVisibleChanged to avoid burning CPU before the panel is ever shown.
        if (IsVisible && !_animationsStarted)
        {
            _animationsStarted = true;
            StartEntranceAndCarousel();
        }
    }

    private void StartEntranceAndCarousel()
    {
        // Mascot Breathing Animation
        var breathingAnimation = new DoubleAnimation
        {
            From = -5,
            To = 5,
            Duration = TimeSpan.FromSeconds(2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        ApexCodeLogo.RenderTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, breathingAnimation);

        // Staggered Entrance Animations
        AnimateEntrance(HeaderSection, 0);
        AnimateEntrance(FeaturesSection, 150);
        AnimateEntrance(GetStartedButton, 300);

        InitializeCarousel();
    }

    private void AnimateEntrance(UIElement element, int beginTimeMs)
    {
        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(600),
            BeginTime = TimeSpan.FromMilliseconds(beginTimeMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slideAnim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(600),
            BeginTime = TimeSpan.FromMilliseconds(beginTimeMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        element.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        if (element.RenderTransform is System.Windows.Media.TranslateTransform transform)
        {
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
        }
    }

    private void InitializeCarousel()
    {
        _featureItems = new[] { FeatureItem1, FeatureItem2, FeatureItem3, FeatureItem4 };
        _indicators = new[] { Indicator0, Indicator1, Indicator2, Indicator3 };

        _carouselTimer?.Stop();
        _carouselTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _carouselTimer.Tick += CarouselTimer_Tick;
        _carouselTimer.Start();
        StartProgressAnimation();
    }

    private void StartProgressAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromSeconds(4)
        };
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    private void CarouselTimer_Tick(object? sender, EventArgs e)
    {
        if (_featureItems.Length == 0) return;
        int nextIndex = (_currentFeatureIndex + 1) % _featureItems.Length;
        TransitionToFeature(nextIndex);
    }

    private void TransitionToFeature(int nextIndex)
    {
        if (nextIndex == _currentFeatureIndex) return;

        var currentCard = _featureItems[_currentFeatureIndex];
        var nextCard = _featureItems[nextIndex];

        // Fade out current
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        currentCard.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        // Fade in next
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        
        var slideIn = new DoubleAnimation
        {
            From = 20,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        nextCard.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        if (nextCard.RenderTransform is System.Windows.Media.TranslateTransform transform)
        {
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideIn);
        }

        _indicators[nextIndex].IsChecked = true;
        _currentFeatureIndex = nextIndex;
        StartProgressAnimation();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_featureItems.Length == 0) return;
        _carouselTimer?.Stop();
        int nextIndex = (_currentFeatureIndex - 1 + _featureItems.Length) % _featureItems.Length;
        TransitionToFeature(nextIndex);
        _carouselTimer?.Start();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_featureItems.Length == 0) return;
        _carouselTimer?.Stop();
        int nextIndex = (_currentFeatureIndex + 1) % _featureItems.Length;
        TransitionToFeature(nextIndex);
        _carouselTimer?.Start();
    }

    private void Indicator_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out int targetIndex))
        {
            _carouselTimer?.Stop(); // Reset timer
            TransitionToFeature(targetIndex);
            _carouselTimer?.Start();
        }
    }

    private void UpdateButtonForConfiguration()
    {
        if (GetStartedButton == null) return;

        if (_isConfigured)
        {
            GetStartedButton.Content = "Let's Get Started";
        }
        else
        {
            GetStartedButton.Content = "Configure API Key";
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var dict = Helpers.ThemeManager.GetThemeDictionaryContainer();
        bool isDark = Helpers.ThemeManager.IsDarkTheme;
        for (int i = this.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var current = this.Resources.MergedDictionaries[i];
            if (current.Contains("AiAssistantThemeMarker") || (current.Source != null && current.Source.ToString().Contains("Theme.xaml")))
            {
                this.Resources.MergedDictionaries.RemoveAt(i);
            }
        }
        this.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary 
        { 
            Source = new Uri(isDark ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute) 
        });
        this.Resources.MergedDictionaries.Add(dict);
    }

    private void GetStartedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConfigured)
        {
            GetStarted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ConfigureApiKeyRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
