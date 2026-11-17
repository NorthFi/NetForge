// root/selfhost/web/rf-console.js
// Staff-only RF telemetry mappings and retention controls. No external JS deps.
'use strict';

const rfMetricNames={
 rssi_dbm:['RSSI','dBm'],rssi_chain0_dbm:['RSSI Chain 0','dBm'],
 rssi_chain1_dbm:['RSSI Chain 1','dBm'],snr_db:['Signal-to-noise','dB'],
 noise_dbm:['Noise floor','dBm'],ccq_pct:['CCQ','%'],
 tx_ccq_pct:['TX CCQ','%'],rx_capacity_mbps:['RX link capacity','Mbps'],
 tx_capacity_mbps:['TX link capacity','Mbps'],cpu_pct:['CPU utilization','%'],
 memory_pct:['Memory utilization','%'],voltage_v:['Supply voltage','V'],
 power_w:['Power draw','W'],temperature_c:['Temperature','°C'],
 frequency_mhz:['Operating frequency','MHz']
};

function rfChart(samples,kind,profile){
 const rows=samples.map(row=>({
  time:Date.parse(kind==='hourly'?row.hour_start:row.observed_at),
  value:Number(kind==='hourly'?row.average:row.value)
 })).filter(x=>Number.isFinite(x.time)&&Number.isFinite(x.value)).slice(-250);
 if(rows.length<2)return '<p class="muted">At least two valid measurements are needed for a time-series chart.</p>';
 const w=760,h=230,l=65,r=18,t=18,b=36;
 let lo=Math.min(...rows.map(x=>x.value)),hi=Math.max(...rows.map(x=>x.value));
 const thresholds=[profile.warning_threshold,profile.critical_threshold]
  .filter(x=>x!==null&&x!==undefined).map(Number).filter(Number.isFinite);
 if(thresholds.length){lo=Math.min(lo,...thresholds);hi=Math.max(hi,...thresholds)}
 const pad=Math.max((hi-lo)*0.08,0.25);lo-=pad;hi+=pad;
 const start=rows[0].time,end=rows[rows.length-1].time;
 const x=n=>l+(n-start)/Math.max(1,end-start)*(w-l-r);
 const y=n=>t+(1-(n-lo)/(hi-lo))*(h-t-b);
 const poly=rows.map(row=>x(row.time).toFixed(1)+','+y(row.value).toFixed(1)).join(' ');
 const unit=rfMetricNames[profile.metric_key]?.[1]||'';
 let bands='';
 for(const [field,color,label] of [['warning_threshold','#da9a21','Warning'],['critical_threshold','#d14a4a','Critical']]){
  if(profile[field]===null||profile[field]===undefined)continue;
  const value=Number(profile[field]);if(!Number.isFinite(value))continue;
  bands+='<line x1="'+l+'" y1="'+y(value).toFixed(1)+'" x2="'+(w-r)+'" y2="'+y(value).toFixed(1)+'" stroke="'+color+'" stroke-dasharray="6 5"/>';
  bands+='<text x="'+(w-r-115)+'" y="'+(y(value)-4).toFixed(1)+'" font-size="11" fill="'+color+'">'+label+' '+esc(value)+'</text>';
 }
 return '<div class="tablewrap"><svg role="img" aria-label="'+esc(profile.metric_key)+' historical measurements" viewBox="0 0 '+w+' '+h+'" style="width:100%;max-height:300px">'+
  '<line x1="'+l+'" y1="'+t+'" x2="'+l+'" y2="'+(h-b)+'" stroke="#8391a3"/>'+
  '<line x1="'+l+'" y1="'+(h-b)+'" x2="'+(w-r)+'" y2="'+(h-b)+'" stroke="#8391a3"/>'+
  bands+'<polyline fill="none" stroke="#7160d9" stroke-width="2.3" points="'+poly+'"/>'+
  '<text x="5" y="'+(t+6)+'" fill="currentColor" font-size="11">'+esc(hi.toFixed(2))+' '+esc(unit)+'</text>'+
  '<text x="5" y="'+(h-b+4)+'" fill="currentColor" font-size="11">'+esc(lo.toFixed(2))+'</text>'+
  '<text x="'+l+'" y="'+(h-8)+'" fill="currentColor" font-size="11">'+esc(new Date(start).toISOString().slice(0,16))+' UTC</text>'+
  '<text x="'+(w-190)+'" y="'+(h-8)+'" fill="currentColor" font-size="11">'+esc(new Date(end).toISOString().slice(0,16))+' UTC</text>'+
  '</svg></div>';
}

function rfConsole(el){
 const allKeys=Object.entries(rfMetricNames);
 el.innerHTML+='<h1>RF and equipment telemetry</h1><p class="sub">Read-only SNMPv3 authPriv scalar measurements. Configure verified OIDs for the exact vendor, radio model, firmware and IF-MIB instance; NetForge does not invent proprietary OIDs.</p><div class="card" id="rf-add-card"><h2>New metric mapping</h2><form id="rf-form"><div class="formgrid"><div><label>SNMPv3 profile</label><select id="rf-device" required></select></div><div><label>Measurement</label><select name="metricKey">'+allKeys.map(([k,v])=>'<option value="'+esc(k)+'">'+esc(v[0])+' ('+esc(v[1])+')</option>').join('')+'</select></div><div><label>Fully qualified numeric OID</label><input name="oid" required maxlength="128" placeholder="Paste verified numeric OID"></div><div><label>Raw value multiplier</label><input type="number" step="any" name="scale" value="1" required></div><div><label>Offset after scaling</label><input type="number" step="any" name="offset" value="0" required></div><div><label>Bad-value direction</label><select name="direction"><option value="below">Below threshold</option><option value="above">Above threshold</option></select></div><div><label>Warning threshold (optional)</label><input type="number" step="any" name="warningThreshold"></div><div><label>Critical threshold (optional)</label><input type="number" step="any" name="criticalThreshold"></div><div><label>Recovery hysteresis margin</label><input type="number" step="any" min="0" name="recoveryMargin" value="0" required></div></div><label><input type="checkbox" name="enabled" style="width:auto"> Start polling this OID with the SNMP device</label><p class="note">Enter warning and critical together, in the same units as the scaled reading. Two consecutive measurements are required before changing alert severity. Raw data is stored even if alerting is disabled.</p><div class="buttons"><button class="primary">Save OID mapping</button></div><p id="rf-error" role="status"></p></form></div><div class="card topgap"><div class="toolbar"><h2>Configured metrics</h2><button id="rf-refresh">Refresh</button></div><div id="rf-records">Loading…</div></div><div class="card topgap"><h2>Measurement history</h2><div id="rf-history">Choose a metric to examine raw and hourly values.</div></div><div class="card topgap"><h2>Highsite RF warning summary</h2><div id="rf-sites">Loading…</div></div><div class="card topgap"><h2>Threshold transition history</h2><div id="rf-events">Loading…</div></div>';
 const form=el.querySelector('#rf-form'),device=el.querySelector('#rf-device'),
  error=el.querySelector('#rf-error'),records=el.querySelector('#rf-records'),
  history=el.querySelector('#rf-history'),events=el.querySelector('#rf-events'),
  sites=el.querySelector('#rf-sites');
 if(role!=='admin')form.closest('.card').hidden=true;
 form.querySelector('[name="metricKey"]').onchange=e=>{
  const key=e.target.value;
  form.querySelector('[name="direction"]').value=
   ['cpu_pct','memory_pct','temperature_c','power_w','voltage_v'].includes(key)?'above':'below';
 };
 const numeric=v=>v===''||v===null||v===undefined?null:Number(v);
 async function loadDevices(){
  const all=await api('/snmp/devices');
  device.innerHTML='<option value="">Select inventoried SNMPv3 device</option>'+
   all.map(x=>'<option value="'+esc(x.id)+'">'+esc(x.name)+' · '+esc(x.address)+'</option>').join('');
 }
 form.onsubmit=async e=>{
  e.preventDefault();error.textContent='';
  const raw=Object.fromEntries(new FormData(form));
  if(!device.value){error.textContent='Select a configured SNMPv3 profile first.';return}
  if(Boolean(raw.warningThreshold)==Boolean(raw.criticalThreshold) && Boolean(raw.warningThreshold)===false){
   // Both empty is valid; alerting remains disabled for this metric.
  }else if(raw.warningThreshold==='' || raw.criticalThreshold===''){
   error.textContent='Provide both warning and critical levels, or neither.';return;
  }
  const enabled=form.querySelector('[name="enabled"]').checked;
  if(enabled&&!confirm('Enable read-only SNMPv3 polling of '+raw.metricKey+' at the verified OID?'))return;
  const payload={deviceId:device.value,metricKey:raw.metricKey,oid:raw.oid,
   scale:Number(raw.scale),offset:Number(raw.offset),enabled,
   direction:raw.direction,warningThreshold:numeric(raw.warningThreshold),
   criticalThreshold:numeric(raw.criticalThreshold),
   recoveryMargin:Number(raw.recoveryMargin),confirm:enabled};
  try{await api('/telemetry/metrics','POST',payload);form.reset();error.textContent='Metric mapping created. Validate first samples against vendor tools.';await draw();}
  catch(err){error.textContent=err.message}
 };
 async function historyFor(profile,resolution='raw'){
  const hours=resolution==='hourly'?2160:168;
  history.textContent='Loading '+resolution+' samples…';
  try{
   const rows=await api('/telemetry/metrics/'+profile.id+'/history?hours='+hours+'&resolution='+resolution);
   const unit=rfMetricNames[profile.metric_key]?.[1]||'';
   history.innerHTML='<h3>'+esc(profile.device)+' — '+esc(rfMetricNames[profile.metric_key]?.[0]||profile.metric_key)+' ('+esc(unit)+')</h3><div class="buttons"><button id="rf-raw">Recent raw readings</button><button id="rf-hourly">Hourly history (90 days)</button></div>'+rfChart(rows,resolution,profile)+'<div class="tablewrap"><table><thead><tr><th>UTC</th><th>Value ('+esc(unit)+')</th>'+(resolution==='hourly'?'<th>Minimum</th><th>Maximum</th><th>Count</th>':'<th>Raw SNMP scalar</th>')+'</tr></thead><tbody>'+rows.slice(-100).reverse().map(row=>'<tr><td>'+esc(row.hour_start||row.observed_at)+'</td><td>'+esc(row.average??row.value)+'</td>'+(resolution==='hourly'?'<td>'+esc(row.minimum)+'</td><td>'+esc(row.maximum)+'</td><td>'+esc(row.samples)+'</td>':'<td>'+esc(row.raw_value)+'</td>')+'</tr>').join('')+'</tbody></table></div>';
   history.querySelector('#rf-raw').onclick=()=>historyFor(profile,'raw');
   history.querySelector('#rf-hourly').onclick=()=>historyFor(profile,'hourly');
  }catch(err){history.textContent=err.message}
 }
 async function policyDialog(profile){
  const d=dialog('<h2>RF metric thresholds</h2><p class="note">Updating a policy resets its active alarm state to normal without erasing existing events. OID and scale cannot be changed; create a distinct monitored profile after testing changes.</p><form id="rf-policy"><label><input type="checkbox" name="enabled" style="width:auto"> Enable polling</label><label>Bad-value direction</label><select name="direction"><option value="below">Below</option><option value="above">Above</option></select><label>Warning threshold</label><input type="number" step="any" name="warningThreshold"><label>Critical threshold</label><input type="number" step="any" name="criticalThreshold"><label>Recovery margin</label><input type="number" step="any" min="0" name="recoveryMargin" required><p id="rf-policy-error" role="status"></p><div class="buttons"><button class="primary">Save policy</button><button type="button" id="rf-close">Cancel</button></div></form>');
  const form=d.querySelector('#rf-policy');
  form.querySelector('[name="enabled"]').checked=profile.enabled;
  form.querySelector('[name="direction"]').value=profile.direction;
  form.querySelector('[name="warningThreshold"]').value=profile.warning_threshold??'';
  form.querySelector('[name="criticalThreshold"]').value=profile.critical_threshold??'';
  form.querySelector('[name="recoveryMargin"]').value=profile.recovery_margin;
  d.querySelector('#rf-close').onclick=()=>d.close();
  form.onsubmit=async e=>{
   e.preventDefault();const raw=Object.fromEntries(new FormData(form));
   const enabled=form.querySelector('[name="enabled"]').checked;
   if((raw.warningThreshold==='')!==(raw.criticalThreshold==='')){
    form.querySelector('#rf-policy-error').textContent='Provide both threshold values or neither.';return;
   }
   if(enabled&&!confirm('Apply and enable polling for '+profile.device+' / '+profile.metric_key+'?'))return;
   try{
    await api('/telemetry/metrics/'+profile.id,'PUT',{enabled,
     direction:raw.direction,warningThreshold:numeric(raw.warningThreshold),
     criticalThreshold:numeric(raw.criticalThreshold),
     recoveryMargin:Number(raw.recoveryMargin),confirm:enabled});
    d.close();await draw();
   }catch(err){form.querySelector('#rf-policy-error').textContent=err.message}
  };
 }
 async function draw(){
  try{
   const [all,siteRows,alertRows]=await Promise.all([
    api('/telemetry/metrics'),api('/telemetry/sites'),api('/telemetry/events')
   ]);
   records.innerHTML='<div class="tablewrap"><table><thead><tr><th>Device</th><th>Measurement</th><th>OID</th><th>Scale</th><th>Last value</th><th>Severity</th><th>Thresholds</th><th>Polling</th><th>Actions</th></tr></thead><tbody>'+all.map(m=>{
    const unit=rfMetricNames[m.metric_key]?.[1]||'';
    return '<tr><td>'+esc(m.device)+'</td><td>'+esc(rfMetricNames[m.metric_key]?.[0]||m.metric_key)+'</td><td>'+esc(m.oid)+'</td><td>'+esc(m.scale)+'</td><td>'+esc(m.last_value===null?'—':m.last_value+' '+unit)+'</td><td>'+esc(m.severity)+'</td><td>'+esc(m.warning_threshold===null?'Off':m.direction+' warn '+m.warning_threshold+' / crit '+m.critical_threshold)+'</td><td>'+esc(m.enabled?'Enabled':'Disabled')+'</td><td><button data-history="'+esc(m.id)+'">History</button>'+(role==='admin'?' <button data-policy="'+esc(m.id)+'">Policy</button>':'')+'</td></tr>';
   }).join('')+'</tbody></table></div>'+(all.length?'':'<p class="muted">No RF/vendor OIDs configured yet.</p>');
   records.querySelectorAll('[data-history]').forEach(b=>b.onclick=()=>{
    const m=all.find(x=>x.id===b.dataset.history);if(m)historyFor(m)
   });
   records.querySelectorAll('[data-policy]').forEach(b=>b.onclick=()=>{
    const m=all.find(x=>x.id===b.dataset.policy);if(m)policyDialog(m)
   });
   sites.innerHTML='<div class="tablewrap"><table><thead><tr><th>Highsite</th><th>Active OIDs</th><th>Warning</th><th>Critical</th></tr></thead><tbody>'+siteRows.map(x=>'<tr><td>'+esc(x.name)+'</td><td>'+esc(x.metrics)+'</td><td>'+esc(x.warnings)+'</td><td>'+esc(x.critical)+'</td></tr>').join('')+'</tbody></table></div>';
   events.innerHTML='<div class="tablewrap"><table><thead><tr><th>UTC</th><th>Device</th><th>Metric</th><th>Previous</th><th>New</th><th>Value</th><th>Upstream suppression</th></tr></thead><tbody>'+alertRows.map(x=>'<tr><td>'+esc(x.observed_at)+'</td><td>'+esc(x.device)+'</td><td>'+esc(x.metric_key)+'</td><td>'+esc(x.previous_severity)+'</td><td>'+esc(x.severity)+'</td><td>'+esc(x.value)+'</td><td>'+esc(x.suppressed?'Yes':'No')+'</td></tr>').join('')+'</tbody></table></div>';
  }catch(err){records.textContent=err.message}
 }
 el.querySelector('#rf-refresh').onclick=draw;
 loadDevices().then(draw).catch(err=>{error.textContent=err.message;draw()});
}

function retentionConsole(el){
 el.innerHTML+='<h1>Telemetry lifecycle and retention</h1><p class="sub">Hourly RF and IF-MIB aggregation followed by bounded pruning. Cleanup is disabled by default and cannot be reversed without a database backup.</p><div class="card"><h2>Retention policy</h2><form id="retention-form"><label><input id="retention-enabled" type="checkbox" style="width:auto"> Enable 6-hourly maintenance and retention cleanup</label><div class="formgrid"><div><label>Raw sample retention, days (7–365)</label><input id="retention-raw" type="number" min="7" max="365" value="90" required></div><div><label>Hourly summary retention, days (30–1825)</label><input id="retention-hourly" type="number" min="30" max="1825" value="365" required></div></div><p class="note">Rolling up completed hours is lossless for minimum, maximum, average and sample count, but individual raw values are permanently removed past the raw-retention limit. Keep verified PostgreSQL backups before enabling.</p><div class="buttons"><button class="primary">Save retention settings</button><button type="button" id="retention-run">Run maintenance now</button><button type="button" id="retention-refresh">Refresh</button></div><p id="retention-result" role="status"></p></form></div>';
 const enabled=el.querySelector('#retention-enabled'),
  raw=el.querySelector('#retention-raw'),hourly=el.querySelector('#retention-hourly'),
  status=el.querySelector('#retention-result');
 async function load(){
  try{
   const cfg=await api('/telemetry/retention');
   enabled.checked=cfg.settings.enabled;raw.value=cfg.settings.raw_days;
   hourly.value=cfg.settings.hourly_days;
   status.textContent='Current policy: '+(enabled.checked?'ENABLED':'disabled')+
    '. Last successful maintenance: '+(cfg.settings.last_run_at||'never');
  }catch(err){status.textContent=err.message}
 }
 el.querySelector('#retention-form').onsubmit=async e=>{
  e.preventDefault();
  if(enabled.checked&&!confirm('Enable automatic destructive cleanup of old telemetry after hourly aggregation? A tested database backup is recommended.'))return;
  try{
   const res=await api('/telemetry/retention','PUT',{
    enabled:enabled.checked,rawDays:Number(raw.value),
    hourlyDays:Number(hourly.value),confirm:true
   });status.textContent=res.note;
  }catch(err){status.textContent=err.message}
 };
 el.querySelector('#retention-run').onclick=async()=>{
  if(!confirm('Execute telemetry rollup and deletion using the SAVED enabled policy? This cannot be undone except by restore.'))return;
  try{
   const res=await api('/telemetry/retention/run','POST',{confirm:true});
   status.textContent=res.note+' RF hourly bins '+res.rfHours+', IF-MIB bins '+res.interfaceHours+
    '; raw RF removed '+res.rfRawDeleted+', raw IF-MIB removed '+res.interfaceRawDeleted+
    '; old RF hourly removed '+res.rfHourlyDeleted+', IF-MIB hourly removed '+res.interfaceHourlyDeleted+'.';
  }catch(err){status.textContent=err.message}
 };
 el.querySelector('#retention-refresh').onclick=load;load();
}
