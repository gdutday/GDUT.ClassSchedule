module GDUT.ClassSchedule

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open HtmlAgilityPack
open Microsoft.ClearScript.V8

type Course =
    { CourseName: string
      CourseId: string
      TeachingClassNames: string array
      CourseCode: string
      Sections: int array
      CourseWeeks: int array
      DayInWeek: int
      Place: string
      Teachers: string array }

    override this.ToString() =
        $"课程名称: {this.CourseName}, 课程编号: {this.CourseId}, 教学班名称: {String.Join(',', this.TeachingClassNames)}, 课程任务代码: {this.CourseCode}, 节次: {String.Join(',', this.Sections)}, 周次: {String.Join(',', this.CourseWeeks)}, 星期: {this.DayInWeek}, 教学场地: {this.Place}, 授课教师: {String.Join(',', this.Teachers)}"

type CourseConverter() =
    inherit JsonConverter<Course>()

    override _.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions) =
        let doc = JsonDocument.ParseValue(&reader)
        let root = doc.RootElement
        let courseName = root.GetProperty("kcmc").GetString()
        let courseId = root.GetProperty("kcbh").GetString()

        let teachingClassNames =
            root
                .GetProperty("jxbmc")
                .GetString()
                .Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)

        let courseCode = root.GetProperty("kcrwdm").GetString()

        let sections =
            root
                .GetProperty("jcdm2")
                .GetString()
                .Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map int

        let courseWeeks =
            root
                .GetProperty("zcs")
                .GetString()
                .Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map int

        let term = root.GetProperty("xq").GetString() |> int
        let place = root.GetProperty("jxcdmcs").GetString()

        let teachers =
            root
                .GetProperty("teaxms")
                .GetString()
                .Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)

        { CourseName = courseName
          CourseId = courseId
          TeachingClassNames = teachingClassNames
          CourseCode = courseCode
          Sections = sections
          CourseWeeks = courseWeeks
          DayInWeek = term
          Place = place
          Teachers = teachers }

    override _.Write(writer: Utf8JsonWriter, value: Course, options: JsonSerializerOptions) =
        writer.WriteStartObject()
        writer.WriteString("kcmc", value.CourseName)
        writer.WriteString("kcbh", value.CourseId)
        writer.WriteString("jxbmc", String.Join(",", value.TeachingClassNames))
        writer.WriteString("kcrwdm", value.CourseCode)
        writer.WriteString("jcdm2", String.Join(",", value.Sections))
        writer.WriteString("zcdm", String.Join(",", value.CourseWeeks))
        writer.WriteNumber("xq", value.DayInWeek)
        writer.WriteString("jxcdmcs", value.Place)
        writer.WriteString("jsxm", String.Join(",", value.Teachers))
        writer.WriteEndObject()


let private uriBuilder () = UriBuilder("http://jxfw.gdut.edu.cn/")

let private getResponse (client: HttpClient) (uri: Uri) =
    async {
        try
            let! response = client.GetAsync uri |> Async.AwaitTask
            return Ok response
        with ex ->
            return Error $"发生错误: {ex.Message}"
    }

let private getContentStringFromResponse (response: HttpResponseMessage) =
    response.Content.ReadAsStringAsync()
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Ok

let private loadJavaScriptFromHtml (engine: V8ScriptEngine) (doc: HtmlDocument) =
    let getScriptFromBlock (scriptBlock: HtmlNode) = scriptBlock.InnerText

    let scriptBlocks = doc.DocumentNode.SelectNodes("//script")
    let scripts = scriptBlocks |> Seq.map getScriptFromBlock
    scripts |> Seq.iter engine.Execute

let getClassSchedule (cookies: CookieCollection) xnxqdm =
    let uriPath = "/xsgrkbcx!xsAllKbList.action"

    let buildUri xnxqdm =
        let builder = uriBuilder ()
        builder.Path <- uriPath
        builder.Query <- $"xnxqdm=%s{xnxqdm}"
        builder.Uri

    let uri = buildUri xnxqdm

    let cookieContainer = CookieContainer()

    cookies
    |> Seq.filter (fun c -> c.Domain = uri.Host)
    |> Seq.iter cookieContainer.Add

    let handler = new HttpClientHandler(CookieContainer = cookieContainer)
    use client = new HttpClient(handler)

    // TODO: 重要！需要记录
    client.DefaultRequestHeaders.Referrer <- Uri "https://jxfw.gdut.edu.cn/xsgrkbcx!getXsgrbkList.action"

    let contentStringResult =
        getResponse client uri
        |> Async.RunSynchronously
        |> Result.bind getContentStringFromResponse

    match contentStringResult with
    | Ok contentString ->
        let pattern = @"(?<=var kbxx = )\[(.*?)\}];"
        let regex = Regex(pattern)
        let regexMatch = regex.Match(contentString)

        match regexMatch with
        | _ when regexMatch.Success && regexMatch.Captures.Count = 1 ->
            let coursesJson =
                regexMatch.Groups[0].Value.Substring(0, regexMatch.Groups[0].Value.Length - 1) // 去掉末尾的';'

            let options = JsonSerializerOptions()
            options.Converters.Add <| CourseConverter()
            let courses = JsonSerializer.Deserialize<Course[]>(coursesJson, options)
            Ok courses
        | _ -> Error "未能从响应中找到课表信息"
    | Error errorValue -> Error errorValue
