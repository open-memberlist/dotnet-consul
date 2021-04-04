using System.Threading;

namespace OpenMemberList
{
    public class IMemberListClient
    {
        public (string host, int port) GetAddress()
        {
            throw new System.NotImplementedException();
        }

        public string[] GetLabels()
        {
            throw new System.NotImplementedException();
        }

        public void UpdateMembers(Member[] currentMembers)
        {
            throw new System.NotImplementedException();
        }

        public string Id { get; set; }
        
        public CancellationToken Shutdown { get; }
    }
}