#define DOUBLE_ENDED_STREAM_DEBUG


namespace MDACS.Database
{
    public class ProgramConfig
    {
        public string metajournal_path;
        public string data_path;
        public string config_path;
        public string auth_url;
        public string ssl_cert_path;
        public string ssl_cert_pass;
        public string universal_records_key_path;
        public string universal_records_key_pass;
        public string universal_records_url;
        public ushort port;
        public string notification_url;
    }
}
