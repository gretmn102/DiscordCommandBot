module EmojiFont.Main
open FsharpMyExtension
open FsharpMyExtension.Either
open DSharpPlus

open Types

type Request = DiscordMessage.UnicodeOrCustomEmoji * string

module Parser =
    open FParsec

    open DiscordMessage.Parser

    type 'Result Parser = Primitives.Parser<'Result, unit>

    let start: Request Parser =
        skipStringCI "emojiFont" >>. spaces
        >>. (pemoji .>> spaces)
        .>>. manySatisfy (fun _ -> true)

let reduce (e: EventArgs.MessageCreateEventArgs) ((emoji, str): Request) =
    let emojiFont emojiImg =
        use m = new System.IO.MemoryStream()
        Core.drawText emojiImg str m
        m.Position <- 0L

        let b = DSharpPlus.Entities.DiscordMessageBuilder()
        b.WithFile("image.png", m) |> ignore

        awaiti (e.Channel.SendMessageAsync b)

    let emojiImgWidth = 32
    match emoji with
    | DiscordMessage.CustomEmoji emoji ->
        let emojiSrc = sprintf "https://cdn.discordapp.com/emojis/%d.png?size=%d" emoji.Id emojiImgWidth
        let emojiImg = WebClientDownloader.getData [] emojiSrc
        emojiFont emojiImg
    | DiscordMessage.UnicodeEmoji emoji ->
        match StandartDiscordEmoji.getEmojiSheet () with
        | Right emojiSheet ->
            use m = new System.IO.MemoryStream()
            if StandartDiscordEmoji.getEmoji emojiSheet emoji m then
                m.ToArray()
                |> Right
                |> emojiFont
            else
                let msg = sprintf "\"%s\" — этот emoji пока что не поддерживается." emoji
                awaiti (e.Channel.SendMessageAsync msg)
        | Left errMsg ->
            emojiFont (Left errMsg)

let exec e msg =
    reduce e msg
