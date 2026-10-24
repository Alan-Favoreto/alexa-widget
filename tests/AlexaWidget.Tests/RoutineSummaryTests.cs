using System.Text.Json.Nodes;

namespace AlexaWidget.Tests;

public class RoutineSummaryTests
{
    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void PhraseRoutineWithSeveralActions()
    {
        var routine = RoutinesApi.Summarize(Parse("""
            {
              "automationId": "amzn1.alexa.automation.1",
              "name": "Boa noite",
              "status": "ENABLED",
              "triggers": [{ "payload": { "utterance": "boa noite" } }],
              "sequence": { "startNode": {
                "@type": "com.amazon.alexa.behaviors.model.SerialNode",
                "nodesToExecute": [
                  { "type": "Alexa.Speak", "operationPayload": { "textToSpeak": "Dorme bem" } },
                  { "type": "Alexa.System.Wait", "operationPayload": { "waitTimeInSeconds": 5 } },
                  { "type": "Alexa.TextCommand", "operationPayload": { "text": "desligar a luz" } },
                  { "type": "Alexa.Music.PlaySearchPhrase" }
                ]
              } }
            }
            """));

        Assert.Equal("amzn1.alexa.automation.1", routine.Id);
        Assert.Equal("Boa noite", routine.Name);
        Assert.True(routine.Enabled);
        Assert.Equal(["boa noite"], routine.Phrases);
        Assert.Equal(["Alexa diz: Dorme bem", "Esperar 5 s", "Comando: desligar a luz", "Music.PlaySearchPhrase"], routine.Actions);
    }

    [Fact]
    public void ScheduleGivenAsAJsonString()
    {
        var routine = RoutinesApi.Summarize(Parse("""
            {
              "automationId": "r2",
              "status": "DISABLED",
              "triggers": [{ "payload": { "schedule": "{\"triggerTime\":\"073000\",\"recurrence\":\"FREQ=WEEKLY;BYDAY=MO,WE,FR\"}" } }],
              "sequence": { "startNode": { "type": "Alexa.Speak", "operationPayload": { "textToSpeak": "Bom dia" } } }
            }
            """));

        Assert.False(routine.Enabled);
        Assert.Equal("07:30", routine.Time);
        Assert.Equal(["MO", "WE", "FR"], routine.Days);
        Assert.Equal(["Alexa diz: Bom dia"], routine.Actions);
        Assert.Equal("Rotina sem nome", routine.Name);
    }

    [Fact]
    public void UnnamedRoutineIsCalledByItsPhrase()
    {
        var routine = RoutinesApi.Summarize(Parse("""
            { "automationId": "r3", "triggers": [{ "payload": { "utterances": ["cheguei", "estou em casa"] } }] }
            """));

        Assert.Equal("\"cheguei\"", routine.Name);
        Assert.Equal(["cheguei", "estou em casa"], routine.Phrases);
    }
}
