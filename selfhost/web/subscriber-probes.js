// root/selfhost/web/subscriber-probes.js
// Staff UI for independently reported subscriber DNS/HTTPS observations.
'use strict';

function subscriberProbeConsole(el){
 el.innerHTML+='<h1>Subscriber-side probes</h1><p class="sub">Authorized Linux probes send DNS resolution and TLS-verified HTTPS response checks from subscriber networks. A missing report means unknown. Evidence from multiple vantage points is not contractual uptime.</p>'+
 '<div class="card"><h2>Enroll a subscriber vantage</h2><form id="sp-enroll"><div class="formgrid"><div><label>Service</label><select name="serviceId" required id="sp-service"><option value="">Choose service</option></select></div>'+
 '<div><label>Vantage label</label><input name="name" maxlength="100" required></div>'+
 '<div><label>Authorized DNS hostname</label><input name="dnsHost" maxlength="253" required></div>'+
 '<div><label>Authorized HTTPS endpoint (port 443)</label><input name="httpsUrl" type="url" maxlength="512" required></div>'+
 '<div><label>Expected HTTP status (2xx)</label><input name="expectedStatus" type="number" min="200" max="299" value="204" required></div>'+
 '<div><label>Expected sample interval (seconds)</label><input name="intervalSeconds" type="number" min="60" max="3600" value="300" required></div></div>'+
 '<p class="note">Obtain customer authorization first. Select dedicated diagnostic endpoints under operator control. Install the agent manually on an authorized subscriber LAN host; enrollment does not install or remotely configure anything.</p>'+
 '<button class="primary">Enroll and issue one-time configuration</button><p id="sp-error" class="error"></p></form></div>'+
 '<div class="card topgap"><div class="toolbar"><h2>Multi-vantage evidence</h2><button id="sp-refresh">Refresh</button></div><p class="note">Fresh DNS/HTTPS samples, CPE management reachability, and recent RADIUS accounting are separate signals. Old and missing data are unknown; a successful check only covers its specified target.</p><div id="sp-results">Loading…</div></div>'+
 '<div class="card topgap"><h2>Probe history</h2><div id="sp-history">Choose a vantage to examine samples.</div></div>';
 const form=el.querySelector('#sp-enroll'),services=el.querySelector('#sp-service'),
  output=el.querySelector('#sp-results'),history=el.querySelector('#sp-history'),
  error=el.querySelector('#sp-error');
 if(role!=='admin')form.closest('.card').hidden=true;
 services.innerHTML+='<optgroup label="Available services">'+(data.services||[]).map(s=>
  '<option value="'+esc(s.id)+'">'+esc(s.account)+' · '+esc(s.username)+'</option>').join('')+'</optgroup>';
 function configuration(result){
  // No token is cached in localStorage or reloaded by the list endpoint.
  const config={reportUrl:location.origin+'/api/probe-report/'+result.id,
   token:result.token,dnsHost:result.dnsHost,httpsUrl:result.httpsUrl,
   expectedStatus:result.expectedStatus,intervalSeconds:result.intervalSeconds};
  const d=dialog('<h2>One-time agent configuration</h2><p class="note">Save securely on the authorized Linux subscriber host as agent.json, mode 0600. This bearer credential grants reporting access only. NetForge cannot recover the token; rotate it if lost. Verify the report URL uses a trusted externally reachable HTTPS endpoint.</p><textarea id="sp-config" readonly rows="11" spellcheck="false"></textarea><div class="buttons"><button id="sp-save">Save agent.json</button><button id="sp-close">Close</button></div>');
  const encoded=JSON.stringify(config,null,2)+'\n';
  d.querySelector('#sp-config').value=encoded;
  d.querySelector('#sp-save').onclick=()=>{
   const blob=new Blob([encoded],{type:'application/json'}),url=URL.createObjectURL(blob);
   const anchor=document.createElement('a');anchor.href=url;anchor.download='agent.json';anchor.click();
   setTimeout(()=>URL.revokeObjectURL(url),1000);
  };
  d.querySelector('#sp-close').onclick=()=>d.close();
  d.addEventListener('close',()=>{d.querySelector('#sp-config').value='';d.remove()},{once:true});
 }
 async function load(){
  try{
   const rows=await api('/assurance/subscriber-probes');
   output.innerHTML='<div class="tablewrap"><table><thead><tr><th>Account</th><th>Service / vantage</th><th>Subscriber evidence</th><th>DNS</th><th>HTTPS</th><th>CPE evidence</th><th>RADIUS evidence</th><th>Last received UTC</th><th>Actions</th></tr></thead><tbody>'+
    rows.map(r=>'<tr><td>'+esc(r.account)+'</td><td>'+esc(r.username)+' / '+esc(r.name)+'</td><td>'+esc(r.subscriber_evidence)+'</td><td>'+esc(r.dns_result??'—')+' · '+esc(r.dns_ms??'—')+' ms</td><td>'+esc(r.https_result??'—')+' · '+esc(r.https_status??'—')+' · '+esc(r.https_ms??'—')+' ms</td><td>'+esc(r.cpe_evidence)+'</td><td>'+esc(r.radius_evidence)+'</td><td>'+esc(r.last_received_at??'Never')+'</td><td>'+
    '<button data-sp-history="'+esc(r.id)+'">History</button>'+
    (role==='admin'?' <button data-sp-rotate="'+esc(r.id)+'">Rotate / re-enable</button>'+
     (r.enabled?' <button data-sp-revoke="'+esc(r.id)+'">Revoke</button>':''):'')+'</td></tr>').join('')+
    '</tbody></table></div>'+(rows.length?'':'<p>No enrolled subscriber vantage points.</p>');
   output.querySelectorAll('[data-sp-history]').forEach(b=>b.onclick=async()=>{
    const entry=rows.find(r=>r.id===b.dataset.spHistory);
    try{
     const samples=await api('/assurance/subscriber-probes/'+entry.id+'/history?hours=168');
     history.innerHTML='<h3>'+esc(entry.name)+' / '+esc(entry.username)+'</h3><p class="note">DNS: '+esc(entry.dns_host)+' · HTTPS: '+esc(entry.https_url)+' · Expected '+esc(entry.expected_status)+'</p>'+
      '<div class="tablewrap"><table><thead><tr><th>Measured UTC</th><th>Received UTC</th><th>Credential generation</th><th>DNS</th><th>DNS ms</th><th>HTTPS</th><th>HTTP status</th><th>HTTPS ms</th></tr></thead><tbody>'+
      samples.slice(-200).reverse().map(s=>'<tr><td>'+esc(s.measured_at)+'</td><td>'+esc(s.received_at)+'</td><td>'+esc(s.generation)+'</td><td>'+esc(s.dns_result)+'</td><td>'+esc(s.dns_ms)+'</td><td>'+esc(s.https_result)+'</td><td>'+esc(s.https_status??'—')+'</td><td>'+esc(s.https_ms)+'</td></tr>').join('')+'</tbody></table></div>';
    }catch(e){history.textContent=e.message}
   });
   output.querySelectorAll('[data-sp-revoke]').forEach(b=>b.onclick=async()=>{
    if(!confirm('Revoke this agent? Reports with its old credential will be rejected.'))return;
    try{await api('/assurance/subscriber-probes/'+b.dataset.spRevoke+'/revoke','POST',{confirm:true});await load()}
    catch(e){notice(e.message,true)}
   });
   output.querySelectorAll('[data-sp-rotate]').forEach(b=>b.onclick=async()=>{
    if(location.protocol!=='https:'){notice('Use HTTPS before rotating credentials.',true);return}
    if(!confirm('Invalidate the previous token and issue new credentials? Update the authorized local agent configuration manually.'))return;
    try{const result=await api('/assurance/subscriber-probes/'+b.dataset.spRotate+'/rotate','POST',{confirm:true});configuration(result);await load()}
    catch(e){notice(e.message,true)}
   });
  }catch(e){output.textContent=e.message}
 }
 form.onsubmit=async e=>{
  e.preventDefault();error.textContent='';
  const f=Object.fromEntries(new FormData(form));
  if(location.protocol!=='https:'){error.textContent='Use the HTTPS staff endpoint before enrolling or exporting credentials.';return}
  if(!confirm('Confirm customer authorization and the selected diagnostic endpoints. Enroll reporting credentials?'))return;
  try{
   const result=await api('/assurance/subscriber-probes','POST',{
    serviceId:f.serviceId,name:f.name,dnsHost:f.dnsHost,
    httpsUrl:f.httpsUrl,expectedStatus:Number(f.expectedStatus),
    intervalSeconds:Number(f.intervalSeconds),confirm:true});
   configuration(result);form.reset();await load();
  }catch(e){error.textContent=e.message}
 };
 el.querySelector('#sp-refresh').onclick=load;load();
}
