using System.Text.Json;

namespace Glasswork.CanvasHost;

internal static class CanvasPageRenderer
{
    public static string Render(object payload, JsonSerializerOptions jsonOptions, string nonce)
    {
        var json = JsonSerializer.Serialize(payload, jsonOptions).Replace("</", "<\\/", StringComparison.Ordinal);
        return Template
            .Replace("__PAYLOAD__", json, StringComparison.Ordinal)
            .Replace("__NONCE__", nonce, StringComparison.Ordinal);
    }

    private const string Template = """
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="icon" href="data:,">
<title>Glasswork task</title>
<style>
:root{color-scheme:light dark}*{box-sizing:border-box}
body{margin:0;padding:24px;background:var(--background-color-default,#fff);color:var(--text-color-default,#1f2328);font:14px/1.5 var(--font-sans,Segoe UI,sans-serif)}
main{max-width:900px;margin:auto}.card,details{border:1px solid var(--border-color-default,#d0d7de);border-radius:12px;padding:16px;margin-top:16px;background:var(--background-color-subtle,transparent)}
h1{margin:0 0 8px;font-size:26px}h2{margin:0 0 8px;font-size:18px}.muted{color:var(--text-color-muted,#656d76)}.error{border-color:var(--true-color-red,#cf222e)}
summary{cursor:pointer;font-weight:600}.artifact-meta{font-size:12px;font-weight:400;margin-left:8px}.artifact-body{margin-top:12px}.artifact-actions{display:flex;gap:8px;flex-wrap:wrap;margin:8px 0}
button{font:inherit;color:inherit;background:var(--button-default-background,#f6f8fa);border:1px solid var(--border-color-default,#d0d7de);border-radius:6px;padding:5px 10px;cursor:pointer}
.inline-link{border:0;padding:0;background:none;color:var(--link-color,#0969da);text-decoration:underline}.blocked-link,.unresolved-link{color:var(--text-color-muted,#656d76)}
pre{white-space:pre-wrap;overflow:auto;max-height:480px;background:var(--background-color-muted,#f6f8fa);padding:12px;border-radius:8px;font-family:var(--font-mono,Consolas,monospace)}
img{display:block;max-width:100%;max-height:540px;object-fit:contain}iframe{display:block;width:100%;height:480px;border:1px solid var(--border-color-default,#d0d7de);border-radius:8px;background:white}
blockquote,.callout{margin:8px 0;padding:8px 12px;border-left:4px solid var(--border-color-accent,#0969da);background:var(--background-color-muted,#f6f8fa)}
.table-scroll{overflow-x:auto}table{border-collapse:collapse}th,td{border:1px solid var(--border-color-default,#d0d7de);padding:6px 8px}.reason{padding:10px;border-radius:6px;background:var(--background-color-muted,#f6f8fa)}
.drift-banner{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:16px;padding:10px 14px;border-radius:8px;background:var(--background-color-attention,#fff8c5);border:1px solid var(--border-color-attention,#d4a72c);color:var(--text-color-default,#1f2328)}
.drift-banner button{flex-shrink:0}
@media(max-width:560px){body{padding:12px}.card,details{padding:12px}h1{font-size:22px}iframe{height:360px}}
@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}pre,.reason,blockquote,.callout{background:#161b22}button{background:#21262d;border-color:#30363d}.card,details,iframe{border-color:#30363d}}
</style></head><body><main id="app"></main>
<script nonce="__NONCE__">
"use strict";
const data=__PAYLOAD__;
const app=document.querySelector("#app");
const query=new URLSearchParams(location.search);
const token=query.get("token")||"";
let activePreview=null;
let previewGeneration=0;

function element(tag,className,text){const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=String(text);return node}
function api(path,params={}){const url=new URL(path,location.origin);url.searchParams.set("token",token);for(const [key,value] of Object.entries(params))url.searchParams.set(key,value);return url}
async function readText(path,params){const response=await fetch(api(path,params),{cache:"no-store"});if(!response.ok){let message="Content is unavailable.";try{message=(await response.json()).message||message}catch{}throw new Error(message)}return response.text()}
async function post(path,payload){const response=await fetch(api(path),{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});if(!response.ok){let message="Action failed.";try{message=(await response.json()).message||message}catch{}throw new Error(message)}}

function renderReference(body,row,reason){
  body.replaceChildren();
  body.append(element("p","reason",reason||row.referenceReason||"Inline content is unavailable."));
  const actions=element("div","artifact-actions");
  if(row.showOpenInObsidian){
    const obsidian=element("button",null,"Open in Obsidian");
    obsidian.addEventListener("click",()=>artifactAction(row,"open_in_obsidian",body));
    actions.append(obsidian);
  }
  const primary=element("button",null,row.canLaunchExternally?"Open externally":"Show in folder");
  primary.addEventListener("click",()=>artifactAction(row,row.primaryAction,body));
  actions.append(primary);
  if(row.canLaunchExternally){const show=element("button",null,"Show in folder");show.addEventListener("click",()=>artifactAction(row,"show_in_folder",body));actions.append(show)}
  body.append(actions);
}
async function artifactAction(row,operation,body){
  try{await post("/api/artifact/action",{taskId:data.projection.taskId,name:row.fileName,operation})}
  catch(error){body.prepend(element("p","error",error.message))}
}
function sourceParams(row){return{task_id:data.projection.taskId,name:row.fileName}}
async function showHtmlSource(row,body){
  previewGeneration++;
  if(activePreview?.body===body)activePreview=null;
  body.dataset.mode="source";
  body.replaceChildren();
  const controls=htmlControls(row,body);
  const source=element("pre",null,"Loading source…");
  body.append(controls,source);
  try{source.textContent=await readText("/api/artifact/source",sourceParams(row))}
  catch(error){renderReference(body,row,error.message)}
}
function htmlControls(row,body){
  const controls=element("div","artifact-actions");
  const source=element("button",null,"Source");source.addEventListener("click",()=>showHtmlSource(row,body));
  const preview=element("button",null,"Preview");preview.addEventListener("click",()=>activatePreview(row,body));
  const open=element("button",null,"Open externally");open.addEventListener("click",()=>artifactAction(row,"open_externally",body));
  controls.append(source,preview,open);return controls
}
function sanitizePreview(raw){
  const doc=new DOMParser().parseFromString(raw,"text/html");
  doc.querySelectorAll("script,iframe,object,embed,link,base,audio,video,source").forEach(node=>node.remove());
  doc.querySelectorAll("meta[http-equiv]").forEach(node=>{if((node.getAttribute("http-equiv")||"").toLowerCase()==="refresh")node.remove()});
  doc.querySelectorAll("*").forEach(node=>{
    for(const attr of [...node.attributes]){
      const name=attr.name.toLowerCase(),value=attr.value.toLowerCase();
      if(name.startsWith("on")||["src","srcset","href","action","formaction","poster"].includes(name)||(name==="style"&&(value.includes("url(")||value.includes("@import")||value.includes("expression("))))node.removeAttribute(attr.name);
    }
  });
  doc.querySelectorAll("style").forEach(node=>{const css=node.textContent.toLowerCase();if(css.includes("url(")||css.includes("@import")||css.includes("expression("))node.remove()});
  const policy=doc.createElement("meta");policy.httpEquiv="Content-Security-Policy";policy.content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";
  doc.head.prepend(policy);
  return "<!doctype html>"+doc.documentElement.outerHTML;
}
function evictPreview(){
  if(!activePreview)return;
  const {row,body}=activePreview;activePreview=null;body.dataset.mode="evicted";body.replaceChildren(htmlControls(row,body),element("p","reason","Preview closed — another preview is active."));
  const reactivate=element("button",null,"Re-activate preview");reactivate.addEventListener("click",()=>activatePreview(row,body));body.append(reactivate);
}
async function activatePreview(row,body){
  const generation=++previewGeneration;
  try{
    const raw=await readText("/api/artifact/source",sourceParams(row));
    if(generation!==previewGeneration)return;
    if(activePreview?.body!==body)evictPreview();
    body.dataset.mode="preview";body.replaceChildren(htmlControls(row,body));
    const frame=element("iframe");frame.setAttribute("sandbox","");frame.setAttribute("title",row.title+" preview");frame.srcdoc=sanitizePreview(raw);body.append(frame);
    activePreview={row,body,frame};
  }catch(error){renderReference(body,row,error.message)}
}
function renderImage(row,body){
  body.dataset.mode="image";
  const controls=element("div","artifact-actions");
  if(row.isSvg){const source=element("button",null,"View source");source.addEventListener("click",async()=>{body.dataset.mode="source";const pre=element("pre",null,"Loading source…");body.replaceChildren(controls,pre);try{pre.textContent=await readText("/api/artifact/source",sourceParams(row))}catch(error){renderReference(body,row,error.message)}});controls.append(source)}
  const image=element("img");image.alt=row.title;image.src=api("/api/artifact/image",sourceParams(row));image.addEventListener("error",()=>renderReference(body,row,"Image could not be decoded."));
  body.append(controls,image);
}
function renderArtifact(row){
  const details=element("details");details.open=row.isExpanded;
  const summary=element("summary",null,row.title);summary.append(element("span","artifact-meta",`${row.kind} · ${row.sizeDisplay} · ${row.timeBadge}`));details.append(summary);
  const body=element("div","artifact-body");body.setAttribute("data-mode","source");details.append(body);
  if(row.showOpenInObsidian){const open=element("button",null,"Open in Obsidian");open.addEventListener("click",()=>artifactAction(row,"open_in_obsidian",body));body.append(open)}
  const load=()=>{
    if(body.dataset.loaded)return;body.dataset.loaded="true";
    if(row.isReference){renderReference(body,row);return}
    if(row.kind==="markdown"){body.dataset.mode="markdown";const rendered=element("div","markdown");rendered.innerHTML=row.renderedBody||"";body.append(rendered);return}
    if(row.kind==="text"){body.dataset.mode="text";body.append(element("pre",null,row.body||""));return}
    if(row.kind==="image"){renderImage(row,body);return}
    if(row.kind==="html"){showHtmlSource(row,body);return}
    renderReference(body,row);
  };
  if(details.open)load();details.addEventListener("toggle",()=>{if(details.open)load()});return details;
}
function renderTask(p){
  app.append(element("h1",null,p.title||p.taskId),element("p","muted",`${p.status.label} · ${p.taskId}`));
  const description=element("section","card");description.append(element("h2",null,"Description"));const descriptionBody=element("div","markdown");descriptionBody.innerHTML=p.descriptionHtml||"<p class='muted'>No description.</p>";description.append(descriptionBody);app.append(description);
  const notes=element("section","card");notes.append(element("h2",null,"Notes"));const notesBody=element("div","markdown");notesBody.innerHTML=p.notesHtml||"<p class='muted'>No notes.</p>";notes.append(notesBody);app.append(notes);
  const subtasks=element("section","card");subtasks.append(element("h2",null,"Subtasks"),element("p",null,`${p.activeSubtasks.length} active · ${p.completedSubtasks.length} completed`));app.append(subtasks);
  if(p.artifactRows.length){const heading=element("h2",null,"Artifacts");heading.style.marginTop="20px";app.append(heading);p.artifactRows.forEach(row=>app.append(renderArtifact(row)))}
}
app.addEventListener("click",event=>{
  const target=event.target.closest("[data-task-id],[data-external-url],[data-vault-path]");if(!target)return;
  if(target.dataset.taskId){const next=new URL(location.href);next.searchParams.set("task_id",target.dataset.taskId);location.assign(next);return}
  if(target.dataset.externalUrl)post("/api/link/action",{url:target.dataset.externalUrl}).catch(()=>{});
  if(target.dataset.vaultPath)post("/api/vault/action",{url:target.dataset.vaultPath}).catch(()=>{});
});
function renderDriftBanner(){
  if(!data.driftDetected)return;
  const banner=element("div","drift-banner");
  banner.append(element("span",null,data.driftMessage||"A newer version of this canvas is available. Reopen this session to update."));
  const dismiss=element("button",null,"Dismiss");dismiss.addEventListener("click",()=>banner.remove());
  banner.append(dismiss);
  app.append(banner);
}
renderDriftBanner();
if(data.kind==="empty"){const card=element("article","card");card.append(element("h1",null,"Glasswork task"),element("p","muted",data.message));app.append(card)}
else if(data.kind==="error"){const card=element("article","card error");card.append(element("h1",null,"Task unavailable"),element("p",null,data.message));app.append(card)}
else renderTask(data.projection);
</script></body></html>
""";
}
