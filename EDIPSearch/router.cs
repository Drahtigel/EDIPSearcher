using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Media.Protection.PlayReady;
using Windows.System.Profile;
using Windows.UI.Input.Inking.Preview;

namespace EDIPSearch
{
    class RouterList
    {
        public List<Router> Routers { get; set; } = new List<Router>();
        public RouterList() { }
        public void SaveRouters(string filepath)
        {
            foreach(Router router in Routers)
            {
                router.Save(filepath);
            }
        }
        public void LoadRouters(string filepath)
        {
            Routers.Clear();
            string[] frouters = Directory.GetFiles(filepath, "*.router");
            foreach(string frouter in frouters)
            {
                Router? router = Router.Load(frouter);
                if(router != null)
                {
                    Routers.Add(router);
                }
            }
        }
       
    }
    [JsonSerializable(typeof(Router))]
    [JsonSerializable(typeof(RouterCommands))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
        // Этот класс остается пустым. 
        // Генератор кода сам наполнит его логикой во время компиляции.
    }
    public class Router 
    {
        [JsonIgnore] private string _exceptionMessage = string.Empty;
        private string _pwd = string.Empty; // Зашифрованный пароль пользователя
        public string Name { get; set; } = string.Empty;
        public string ExternalIP { get; set; } = string.Empty;
        public string InternalIP { get; set; } = string.Empty;
        public bool UseExternalIP { get; set; } = false; //Использовать внешний IP для подключения к роутеру
        [JsonIgnore] public RouterCommands Commands { get; set; } = new RouterCommands();
        public string RouterProfile { get; set; } = string.Empty;
        public string RouterFilename { get; set; } = string.Empty;
        public int SSHPort { get; set; } = 22;
        public bool StorePassword { get; set; } = false;
        public string Username { get; set; } = string.Empty;
        [JsonIgnore] public string Password { get; set; } = string.Empty;
        [JsonIgnore] public string ExceptionMessage { get { return _exceptionMessage; } }
        public bool CheckConnection()
        {
            string host = string.Empty;
            if (UseExternalIP) host = ExternalIP; else host = InternalIP;
            int port = SSHPort;
            string username = Username;
            string pwd = Password;
            SshClient client = new SshClient(host, port, username, pwd);
            try
            {
                client.Connect();
                return true;
            }
            catch (Exception e)
            {
                _exceptionMessage = e.Message;
                return false;
            }

        }
        public string RunCommand(int CommandIndex, string[] args)
        {
            _exceptionMessage = string.Empty;
            if ((CommandIndex < 0) || (CommandIndex >= Commands.Count))
            {
                _exceptionMessage = "Invalid command index";
                return string.Empty;
            }
            string command = Commands.Commands[CommandIndex].BuildSimpleString(args);
            if (!Commands.Commands[CommandIndex].IsValidCommand)
            {
                _exceptionMessage = "Invalid command params";
                return string.Empty;
            }
            return RunPreparedCommand(command);


        }
        private string RunPreparedCommand(string command)
        {
            string host = string.Empty;
            if (UseExternalIP) host = ExternalIP; else host = InternalIP;
            int port = SSHPort;
            string username = Username;
            string pwd = Password;
            SshClient client = new SshClient(host, port, username, pwd);
            try
            {
                client.Connect();
                // return true;
                //client.CreateCommand(command);
                SshCommand cmd = client.RunCommand(command);
                return cmd.Result;

            }
            catch (Exception e)
            {
                _exceptionMessage = e.Message;
                // return false;
                return string.Empty;
            }
        }
        public void LoadRouterProfile()
        {
            if (this.RouterFilename == string.Empty) return;
            LoadRouterProfile(this.RouterFilename);
        }
        public void LoadRouterProfile(string filename)
        {
            Commands = RouterCommands.Load(filename);
            if(Commands != null)
            {
                this.RouterProfile = Commands.ProfileName;
                this.RouterFilename = filename;
            }
        }
        public void Save(string filepath)
        {
            string filename = Path.Combine(filepath, this.Name + ".router");
            if(File.Exists(filename)) File.Delete(filename);
            FileStream fs = File.Create(filename);
            JsonSerializer.Serialize(fs, this);
            fs.Flush();
            fs.Close();
        }
        static public Router? Load(string filename) 
        { 
            if(!File.Exists (filename)) return null;
            Router? rez = null;
            FileStream fs = File.OpenRead(filename);
            rez = (Router?)JsonSerializer.Deserialize(fs, typeof(Router));
            fs.Close();
            return rez;
        }
    }
    public class RouterCommand
    {
        [JsonIgnore]private bool _cmdValid = false;
        public string CommandName { get; set; } = string.Empty;
        public string CommandTemplate {  get; set; } = string.Empty;
        public string ParamChar { get; set; } = "%";
        public ObservableCollection<string>IncrementalParams { get; set; } = new ObservableCollection<string>();
        public RouterCommand(string commandName = "", string commandTemplate = "")
        {
            this.CommandName = commandName;
            this.CommandTemplate = commandTemplate;
        }
        [JsonIgnore] public bool IsValidCommand { get { return _cmdValid; } }
        [JsonIgnore]
        public bool IsEmpty { get
            {
                return (CommandTemplate == string.Empty || CommandTemplate.Trim() == "");
            } }
        //Простая строка имеет вид /ip firewall address-list add address=%0 list=%1
        public string BuildSimpleString(string[] args)
        {
            _cmdValid = true;
            if(args.Length==0) return CommandTemplate;
            string rez = CommandTemplate;
            _cmdValid = ValidateTemplate(args);
            for (int i = 0; i < args.Length; i++)
            {
                rez = rez.Replace(ParamChar + i.ToString(), args[i]);
            }
            return rez;
        }
        private bool ValidateTemplate(string[] args)
        {
            //Проверка соответствия переданных аргументов и перечисленных
            //в шаблоне команды. Если количество или сами аргументы не совпадают
            //значит выходная строка будет неверна.
            string s = CommandTemplate;
            int lastFoundIndex = -1;
            int found = 0;
            List<string> TemplateParams = new List<string>();
            lastFoundIndex = s.IndexOf(ParamChar, 0);
            while(lastFoundIndex>-1)
            {
                int ln = 0;
                ln = s.IndexOf(" ", lastFoundIndex);
                if(ln == -1) ln = s.Length-1;
                string p = s.Substring(lastFoundIndex, ln-lastFoundIndex);
                //nextIndex
                int nextIndex = p.IndexOfAny(" )".ToCharArray());
                if(nextIndex>-1)  p = p.Substring(ParamChar.Length, nextIndex-1);
                TemplateParams.Add(p);
                //s = s.Replace(p,"");
                string p1 = s.Substring(0, lastFoundIndex);
                string p2 = s.Substring(ln, s.Length - ln);
                s = p1 + p2;
                found += 1;
                lastFoundIndex = s.IndexOf(ParamChar, 0);
            }
            if (found != args.Length) return false;
            for (int i = 0; i < args.Length; i++)
            {
                bool found_e = false; 
                foreach(string elem in TemplateParams)
                {
                    string arg =  i.ToString();
                    if (elem == arg)
                    {
                        found_e = true;
                        break;
                    }
                }
                if (!found_e) return false;
            }
            return true;
        }
 
        //Инкрементальная строка имеет вид /ip firewall address-list add
        //И массив IncrementalParams {"address", "list"}
        //аргументы должны идти в том же порядке, что и параметры.
        public string BuildIncrementalString(string[] args)
        {
            if (args.Length == 0) return CommandTemplate;
            string rez = CommandTemplate;
            for (int i = 0; i < args.Length; i++)
            {
                if(i<this.IncrementalParams.Count)
                {
                    rez += " " + IncrementalParams[i] + " = " + args[i];
                }
            }
            return rez;
        }
    }
    public class RouterCommands
    {
        [JsonIgnore] private string _exceptionMessage = string. Empty;
        public string ProfileName { get; set; } = string.Empty;
        [JsonInclude]public List<RouterCommand> Commands = new List<RouterCommand>();
        [JsonIgnore]public int Count {  get { return Commands.Count; } }
        public RouterCommands()
        {
            Commands.Add(new RouterCommand("Получить список адресов"));
            //Вывод списка адресов из адрес-листа:
            // /ip firewall address-list print without-paging where (list=ED)&&(!disabled)
            Commands.Add(new RouterCommand("Добавить адрес в список"));
            //Добавить адрес в список:
            // /ip firewall address-list add address=%0 list=%1
            Commands.Add(new RouterCommand("Получить имена списков адресов"));
        }
        public void Save(string filename)
        {
            try
            {
                if (File.Exists(filename)) File.Delete(filename); // Для исключения ошибки удаляем предыдущий файл
                FileStream fs = File.Open(filename, FileMode.Create, FileAccess.ReadWrite);
                JsonSerializer.Serialize(fs, this);
                fs.Flush();
                fs.Close();
                _exceptionMessage = string.Empty;
            }
            catch (Exception ex)
            {
                _exceptionMessage = ex.Message;
            }
        }
        static public RouterCommands Load(string filename) {
            RouterCommands? rez = new RouterCommands();
            try
            {
                FileStream fs = File.Open(filename, FileMode.Open, FileAccess.ReadWrite);
                rez = (RouterCommands?)JsonSerializer.Deserialize(fs,typeof(RouterCommands));
                fs.Close();
                if (rez != null)
                {
                    return rez;
                }
                else
                {
                    rez = new RouterCommands();
                    rez._exceptionMessage = "Загрузка профиля null";
                    return rez;
                }
            }
            catch (Exception ex)
            {
                rez = new RouterCommands();
                rez._exceptionMessage = ex.Message;
                return rez;
            }
        }
    }
}
