using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EDIPSearch.Models;
using EDIPSearch.Network;

namespace EDIPSearch
{
    public partial class frmMikrotikRestConfig : Window
    {
        private List<MikrotikConfig>? _routers;
        private MikrotikConfig? _selectedRouter;
        private bool _isBinding = false;

        public frmMikrotikRestConfig()
        {
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            _routers = RouterStorage.Load();
            lstRouters.ItemsSource = null;
            lstRouters.ItemsSource = _routers;
        }

        private void LstRouters_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstRouters.SelectedItem is MikrotikConfig router)
            {
                _isBinding = true;
                _selectedRouter = router;
                gridEditor.Visibility = Visibility.Visible;

                txtName.Text = router.Name;
                txtIp.Text = router.InternalIp;
                txtPort.Text = router.Port.ToString();
                chkSsl.IsChecked = router.UseSsl;
                txtUser.Text = router.Username;
                txtPass.Password = router.Password;
                cmbWanInterface.Text = router.WanInterface;
                cmbWanInterface.ItemsSource = null; // Сбрасываем старый список при переключении роутеров


                cmbAddressLists.Text = router.TargetAddressList;
                cmbAddressLists.ItemsSource = null; // Очищаем старые варианты при смене роутера

                lblTestStatus.Text = string.Empty;
                _isBinding = false;
            }
            else
            {
                gridEditor.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var newRouter = new MikrotikConfig { Name = "Новый Mikrotik REST" };
            if(_routers==null) _routers = new List<MikrotikConfig>();
            _routers.Add(newRouter);
            lstRouters.ItemsSource = null;
            lstRouters.ItemsSource = _routers;
            lstRouters.SelectedItem = newRouter;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (lstRouters.SelectedItem is MikrotikConfig router)
            {
                if (_routers != null)
                {
                    _routers.Remove(router);
                    lstRouters.ItemsSource = null;
                    lstRouters.ItemsSource = _routers;
                    gridEditor.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void TxtPass_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_isBinding && _selectedRouter != null)
                _selectedRouter.Password = txtPass.Password;
        }

        private void ApplyFormToModel()
        {
            if (_selectedRouter == null) return;
            _selectedRouter.Name = txtName.Text;
            _selectedRouter.InternalIp = txtIp.Text;
            _selectedRouter.Port = int.TryParse(txtPort.Text, out int p) ? p : 443;
            _selectedRouter.UseSsl = chkSsl.IsChecked ?? true;
            _selectedRouter.Username = txtUser.Text;
            _selectedRouter.Password = txtPass.Password;
            _selectedRouter.TargetAddressList = cmbAddressLists.Text; // Поддерживает ручной ввод или выбор
                                                                      // _selectedRouter.WanInterface = txtWanInterface.Text; // Сбор из UI
            _selectedRouter.WanInterface = cmbWanInterface.Text;



        }
        private async void BtnFetchInterfaces_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRouter == null) return;
            ApplyFormToModel();

            btnFetchInterfaces.IsEnabled = false;
            var client = new MikrotikRestClient(_selectedRouter);

            // Запрашиваем реальный список интерфейсов с этого Mikrotik
            var availableInterfaces = await client.GetInterfaceNamesAsync();

            if (availableInterfaces.Count > 0)
            {
                string currentSelection = cmbWanInterface.Text;
                cmbWanInterface.ItemsSource = availableInterfaces;
                cmbWanInterface.Text = currentSelection; // Сохраняем текущий выбор пользователя
            }
            else
            {
                MessageBox.Show("Не удалось получить список интерфейсов. Проверьте настройки связи.", "Ошибка API", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            btnFetchInterfaces.IsEnabled = true;
        }

        private async void BtnFetchLists_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRouter == null) return;
            ApplyFormToModel();

            btnFetchLists.IsEnabled = false;
            var client = new Network.MikrotikRestClient(_selectedRouter);

            // Запрашиваем уникальные списки из Mikrotik
            var availableLists = await client.GetAddressListNamesAsync();

            if (availableLists.Count > 0)
            {
                string currentSelection = cmbAddressLists.Text;
                cmbAddressLists.ItemsSource = availableLists;
                cmbAddressLists.Text = currentSelection;
                MessageBox.Show($"Найдено {availableLists.Count} списков файрвола.", "Mikrotik API", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Не удалось получить списки. Проверьте связь или права пользователя.", "Ошибка API", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            btnFetchLists.IsEnabled = true;
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRouter == null) return;
            ApplyFormToModel();

            btnTest.IsEnabled = false;
            lblTestStatus.Text = "Проверка...";
            lblTestStatus.Foreground = Brushes.Orange;

            var client = new Network.MikrotikRestClient(_selectedRouter);
            var result = await client.TestConnectionAsync();

            switch (result)
            {
                case ConnectionStatus.Success:
                    lblTestStatus.Text = "Связь установлена!";
                    lblTestStatus.Foreground = Brushes.Green;
                    break;
                case ConnectionStatus.Unauthorized:
                    lblTestStatus.Text = "Ошибка: Нет прав / неверный логин/пароль.";
                    lblTestStatus.Foreground = Brushes.Red;
                    break;
                case ConnectionStatus.Unreachable:
                    lblTestStatus.Text = "Роутер недоступен (Таймаут/IP).";
                    lblTestStatus.Foreground = Brushes.Red;
                    break;
            }
            btnTest.IsEnabled = true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            ApplyFormToModel();
            if (_routers != null)
            {
                RouterStorage.Save(_routers);
                MessageBox.Show("Конфигурация REST API успешно сохранена!", "Сохранено", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadData();
            }
        }
    }
}
