module Boosters.Main
open FsharpMyExtension
open FsharpMyExtension.Either
open DSharpPlus

open Shared
open Types
open Model

type Request =
    | SetSetting of ChannelId * MessageTemplate.Message
    | GetSetting

module Parser =
    open FParsec

    open DiscordMessage.Parser

    let setSettingName = "boostersSetSetting"
    let getSettingName = "boostersGetSetting"

    let pchannelTemplateMessage cmdName =
        skipStringCI cmdName >>. spaces
        >>. tuple2
                (pchannelMention .>> spaces)
                MessageTemplate.Message.parser

    let start =
        choice [
            pchannelTemplateMessage setSettingName |>> SetSetting
            skipStringCI getSettingName >>% GetSetting
        ]

type State =
    { GuildBoostersSettings: Boosters.GuildSettings }

let reduce (e: EventArgs.MessageCreateEventArgs) msg (state: Boosters.GuildSettings) =
    match msg with
    | SetSetting(channelId, messageTemplate) ->
        let guild = e.Guild
        let currentMember = getGuildMember guild e.Author
        let replyMessage =
            await (e.Channel.SendMessageAsync "Processing...")

        if (currentMember.Permissions &&& Permissions.Administrator = Permissions.Administrator) then
            let state: Boosters.GuildSettings =
                let template = MessageTemplate.Message.toString messageTemplate

                match Map.tryFind e.Guild.Id state with
                | Some setting ->
                    let setting =
                        { setting with
                            OutputChannelId = channelId
                            Message = template
                        }

                    Boosters.replace setting

                    Map.add guild.Id setting state
                | None ->
                    let x = Boosters.insert (guild.Id, channelId, template)
                    Map.add guild.Id x state

            awaiti (replyMessage.ModifyAsync(Entities.Optional "Booster setting has been set"))

            state
        else
            awaiti (replyMessage.ModifyAsync(Entities.Optional "You don't have administrative permission"))

            state

    | GetSetting ->
        let message =
            let sendErrorMessage () =
                let b = Entities.DiscordMessageBuilder()
                let embed = Entities.DiscordEmbedBuilder()
                embed.Description <-
                    [
                        "```"
                        sprintf ".%s <channel_mention|channel_id>" Parser.setSettingName
                        sprintf "%s boosted the server!" (MessageTemplate.Part.toString MessageTemplate.Part.UserMention)
                        "```"
                    ] |> String.concat "\n"

                b.Embed <- embed.Build()
                b

            match Map.tryFind e.Guild.Id state with
            | Some setting ->
                let b = Entities.DiscordMessageBuilder()
                let embed = Entities.DiscordEmbedBuilder()
                embed.Color <- Entities.Optional.FromValue(Entities.DiscordColor("#2f3136"))
                embed.Description <-
                    [
                        "```"
                        Json.ser {| OutputChannelId = setting.OutputChannelId; Message = setting.Message |}
                        "```"
                    ] |> String.concat "\n"

                b.Embed <- embed.Build()
                b

            | None ->
                sendErrorMessage ()

        awaiti (e.Channel.SendMessageAsync (message))

        state

type Req =
    | Request of EventArgs.MessageCreateEventArgs * Request
    | Handle of EventArgs.GuildMemberUpdateEventArgs

module IReadOnlyList =
    open System.Collections.Generic

    /// difference of **sorting** arrays
    let difference compare (xs: _ IReadOnlyList) (ys: _ IReadOnlyList) =
        let rec f acc i j =
            if i < xs.Count then
                if j < ys.Count then
                    let x = xs.[i]

                    if compare x ys.[j] = 0 then
                        f acc (i + 1) (j + 1)
                    elif compare x ys.[j] < 0 then
                        f acc i (j + 1)
                    else
                        f (x::acc) (i + 1) j
                else
                    let rec f acc i =
                        if i < xs.Count then
                            f (xs.[i]::acc) (i + 1)
                        else
                            acc
                    f acc i
            else
                acc
        List.rev (f [] 0 0)

    open Fuchu

    [<Tests>]
    let differenceTests =
        testList "difference" [
            testCase "testCase1" (fun _ ->
                let compare x y =
                    if x > y then -1
                    elif x < y then 1
                    else 0

                let xs =
                    [
                        difference compare [1..5] [2; 4; 5; 6; 7], [1; 3]
                        difference compare [1..5] [1; 4; 7], [2; 3; 5]
                        difference compare [2; 3] [1; 2; 3], []
                        difference compare [2; 3] [3], [2]
                        difference compare [1..5] [1; 2; 3], [4; 5]
                    ]
                xs
                |> List.iteri (fun i (act, exp) ->
                    Assert.Equal(string i, act, exp)
                )
            )
        ]
    // run differenceTests

let mainReduce req (state: State) =
    match req with
    | Handle e ->
        let addedRoles =
            let compare x y =
                if x > y then -1
                elif x < y then 1
                else 0
            let f = Array.ofSeq >> Array.sortBy (fun (r: Entities.DiscordRole) -> r.Id)
            (f e.RolesBefore)
            |> IReadOnlyList.difference
                (fun (x: Entities.DiscordRole) y -> compare x.Id y.Id)
                (f e.RolesAfter)
        if not <| List.isEmpty addedRoles then
            match Map.tryFind e.Guild.Id state.GuildBoostersSettings with
            | Some setting ->
                match e.Guild.GetChannel setting.OutputChannelId with
                | null -> ()
                | channel ->
                    addedRoles
                    |> List.iter (fun role ->
                        match role.Tags with
                        | null -> () // returns if the role is created while the bot is running
                        | tags ->
                            if tags.IsPremiumSubscriber then
                                MessageTemplate.Message.parse setting.Message
                                |> Either.iter (fun msg ->
                                    let msg = MessageTemplate.Message.substitute e.Member.Mention e.Member.Username msg

                                    try
                                        awaiti (channel.SendMessageAsync msg)
                                    with _ -> ()
                                )
                    )
            | None -> ()

        state

    | Request(e, req) ->
        {state with
            GuildBoostersSettings = reduce e req state.GuildBoostersSettings
        }

let m: MailboxProcessor<Req> =
    let init = {
        GuildBoostersSettings = Boosters.getAll ()
    }

    MailboxProcessor.Start (fun mail ->
        let rec loop (state: State) =
            async {
                let! req = mail.Receive()
                let state =
                    try
                        mainReduce req state
                    with e ->
                        printfn "%A" e
                        state

                return! loop state
            }
        loop init
    )

let exec e msg =
    m.Post (Request(e, msg))

let handle e =
    m.Post (Handle e)
