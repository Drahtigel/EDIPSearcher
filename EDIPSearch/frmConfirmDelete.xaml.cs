using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace EDIPSearch;

public partial class frmConfirmDelete : Window
{
    public bool IsConfirmed { get; private set; } = false;

    public frmConfirmDelete(List<string> selectedAddresses)
    {
        InitializeComponent();
        FormatMessage(selectedAddresses);
    }

    /// <summary>
    /// Форматирует вывод согласно правилу: если <= 3 — пишем адреса, если > 3 — пишем количество.
    /// </summary>
    private void FormatMessage(List<string> addresses)
    {
        if (addresses == null || addresses.Count == 0)
        {
            txtMessage.Text = "Объекты не выбраны.";
            btnConfirm.IsEnabled = false;
            return;
        }

        if (addresses.Count <= 3)
        {
            // Показываем сами адреса через запятую
            txtMessage.Text = string.Join(", ", addresses);
        }
        else
        {
            // Показываем только общее количество объектов
            txtMessage.Text = $"ВСЕГО ОБЪЕКТОВ: {addresses.Count} шт.";
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            this.DragMove();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        this.DialogResult = true;
        this.Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        this.DialogResult = false;
        this.Close();
    }
}
