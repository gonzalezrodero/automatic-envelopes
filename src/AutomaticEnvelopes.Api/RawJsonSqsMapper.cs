using Amazon.SQS.Model;
using System.Text;
using Wolverine;
using Wolverine.AmazonSqs;

namespace AutomaticEnvelopes.Api;

public class RawJsonSqsMapper : ISqsEnvelopeMapper
{
    public string BuildMessageBody(Envelope envelope) => Encoding.UTF8.GetString(envelope.Data!);

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope) => [];

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes) 
        => envelope.Data = Encoding.UTF8.GetBytes(messageBody);
}