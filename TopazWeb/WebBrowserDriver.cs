using NLog;
using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Topaz.Web
{
    public class WebBrowserDriver
    {
        private IWebDriver driver;

        private ScreenshotManager shotManager;

        private readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public enum SyncMethodEnum { PAGELOAD, AJAX, NONE };

        private Object returnValue;

        private string currentAction;

        private bool doNetworkWait;

        // timings in milliseconds for BUSY browser wait
        private int BUSY_browser_setTimeout = 200;
        private int BUSY_browser_response_setTimeout = 220;     // default time before calibration 
        private int BUSY_mv_average_queue_size = 10;

        // Location Lookup API
        public enum API_RESPONSE { URL, METHOD, POSTDATA, RESPONSE_TEXT, STATUS_CODE, STATUS_TEXT };


        public WebBrowserDriver(IWebDriver initialisedDriver)
        {
            driver = initialisedDriver;
            shotManager = new ScreenshotManager();
            Logger.Info($"Using a BUSY timeout of {BUSY_browser_setTimeout} (ms)");
            Logger.Info($"Using a BUSY response timeout of {BUSY_browser_setTimeout} (ms)");
        }

        public IWebDriver GetWebDriverInstance()
        {
            return driver;
        }

        public bool doBrowserAction(Action<IWebDriver> browserAction,
                                    String actionDesc,
                                    Func<IWebDriver, bool> before = null,
                                    Func<IWebDriver, bool> after_beforesync = null,
                                    Func<IWebDriver, bool> after=null, 
                                    bool doReturnValue=false, 
                                    SyncMethodEnum sync=SyncMethodEnum.NONE, 
                                    bool isNewPageWithAjax=false, 
                                    bool doRetries=false)
        {
            bool result = false;

            // log action
            Logger.Info($"ACTION: {actionDesc}");
            currentAction = actionDesc;

            // clear return value if using it
            if (doReturnValue == true)
            {
                setReturnvalue(null);
            }

            if (before != null)
            {
                if (before(driver) == true)
                    Logger.Info("BEFORE function returned PASS");
                else
                    Logger.Error("BEFORE function returned FAIL");
            }

            browserAction(driver);

            if (after_beforesync != null)
            {
                if (after_beforesync(driver) == true)
                    Logger.Info("AFTER_BEFORE_SYNC function returned PASS");
                else
                    Logger.Error("AFTER_BEFORE_SYNC function returned FAIL");
            }

            switch (sync)
            {
                case SyncMethodEnum.PAGELOAD:
                    WaitForPageLoad();
                    break;

                case SyncMethodEnum.AJAX:
                    WaitForAjax();
                    break;

                case SyncMethodEnum.NONE:
                    break;
            }

            if (after != null)
            {
                if (after(driver) == true)
                    Logger.Info("AFTER function returned PASS");
                else
                    Logger.Error("AFTER function returned FAIL");
            }

            if (isNewPageWithAjax == true)
            {
                // retry if errors
                int retryAttempts = 0;
                bool done = false;
                do
                {
                    try
                    {
                        LoadJavascriptContext();

                        done = true;
                    }
                    catch(Exception ex)
                    {
                        retryAttempts++;
                        Logger.Warn($"Unable to insert javascript sync code into page: {ex.Message}");

                        Thread.Sleep(1000);
                    }
                    finally
                    {
                        if (retryAttempts > 3) done = true;                       
                    }
                }
                while(done == false);
            }

            return result;
        }

        public void setReturnvalue(Object retObj)
        {
            returnValue = retObj;
        }

        public Object getReturnValue()
        {
            return returnValue;
        }

        public void EnableNetworkWait(bool doit)
        {
            doNetworkWait = doit;
        }

        private void initJavascriptWaits()
        {
            String isjQueryBusy_js = @"

// check for outstanding network calls
window.isCallingAjax = function() {
  return window.requestCount > 0;
}


// use Mutations to observe DOM changes
window.mutationCount = 0;
window.prev_mutationCount = 0;

MutationObserver.prototype.getCount = function() {
  return window.mutationCount;
}

MutationObserver.prototype.haveNewMutations = function() {
  if (window.prev_mutationCount == window.mutationCount) {
    st = false;
  }
  else {
    st = true;
  }

  window.prev_mutationCount = window.mutationCount

  return st;
}

window.mutationObserver = new MutationObserver(function(mutations) {
  mutations.forEach(function(mutation) {

 // don't write mutation logs to console as it can really slow down the browser if there are too many e.g. Interact5 ordering process generated 80K mutations
//    console.log(mutation);

    window.mutationCount++;
  });
});

window.mutationObserver.observe(document.documentElement, {
  attributes: true,
  characterData: true,
  childList: true,
  subtree: true,
  attributeOldValue: true,
  characterDataOldValue: true
});


// variables for browser BUSY code
window.moving_ave_diff = [];
window.idx = 0;
window.moving_diff = 0;

// running calculation of moving average response time
window.doMovingAverage_BUSYwait = function(a, callback, response_setTimeout, queue_size){
    var b = new Date();
    var diff = Math.abs(b - window.a); 
//    console.log('Ran after ' + diff + ' milliseconds');
    window.moving_ave_diff[window.idx++ % window.queue_size] = diff;
    if (window.idx > window.queue_size) {
      window.iidx=10;
    }
    else
    {
      window.iidx = window.idx;
    }

    // old style for support of IE11
    window.moving_diff = window.moving_ave_diff.reduce(function (a, b) {
      return a + b;
    }, 0) / window.iidx;

// debug
//    console.log('Ran after (moving diff) ' + window.moving_diff + ' milliseconds');
//    console.log('Moving Ave Diff array: ', window.moving_ave_diff);
//    console.log('idx: ', window.idx);

    window.callback( (window.moving_diff > window.response_setTimeout) ? true : false );  
}
";

            
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            js.ExecuteScript(isjQueryBusy_js);

            // give the browser time to process javascript
     //       Thread.Sleep(1000);
        }

        private void WaitForAjax()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            //       Int64 wait_status = 0;
            Boolean isAjax_active = true;

            //        Thread.Sleep(100);

            // wait for browser to start doing something - do a BUSY wait
            Boolean browserBusy = false;
            Stopwatch timer = new Stopwatch();
            timer.Start();

            while (timer.Elapsed < new TimeSpan(0, 0, 0, 0, 800) && browserBusy == false)
            {
                if (DoBUSYWait(timer))
                {
                    browserBusy = true;
                    continue;
                }
            }

            if (browserBusy == false)
            {
                Logger.Info("Could not detect the start of browser activity after: {0}", timer.Elapsed.ToString());
            }

            do
            {
                // wait for network
                if (DoNetworkWait(timer))
                {
                    // small wait for network traffic to complete
                    Thread.Sleep(300);
                    continue;
                }

                // wait for DOM activity
                if (DoDOMWait(timer))
                {
                    // small wait to prevent large numnber of mutations from overloading browser
                    Thread.Sleep(300);
                    continue;
                }

                // wait for not Busy
                if (DoBUSYWait(timer))
                {
                    Logger.Info("Waiting on BUSY browser");

                    // don't use sleep as it will interfere with moving average which already contains a timer
                    //   Thread.Sleep(300);
                    continue;
                }

                // no browser activity detected so have a small sleep and check again just to make sure
                Thread.Sleep(300);

                // confirm browser activity finished
                isAjax_active = (Boolean)js.ExecuteScript("return document.readyState != 'complete' || window.isCallingAjax()  || mutationObserver.haveNewMutations()")
                                && DoBUSYWait(timer);

                if (isAjax_active == true)
                {
                    Logger.Info("More browser activity detected! Running sync again: {0}", timer.Elapsed.ToString());
                }
                else
                {
                    Logger.Info("No more browser activity of any kind detected: {0}", timer.Elapsed.ToString());
                }

            }
            while (isAjax_active == true);

            AddScreenshot($"{currentAction} Browser sync completed");
        }

        // calibrate quiet time response time for BUSY wait
        public void Calibrate()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            String isBusy2_js = $@"
window.a = new Date();
window.callback = arguments[arguments.length - 1];  // Selenium callback function
window.iidx=0;
window.queue_size = {BUSY_mv_average_queue_size};
window.response_timeout = {BUSY_browser_response_setTimeout};

setTimeout(window.doMovingAverage_BUSYwait, {BUSY_browser_setTimeout}, window.a, window.callback, {BUSY_browser_response_setTimeout}, {BUSY_mv_average_queue_size});
";

            Logger.Info("Calibrating BUSY response timeout...");

            initJavascriptWaits();

            for (int i=0; i<30; i++)
            {
                js.ExecuteAsyncScript(isBusy2_js);
                Thread.Sleep(1);
            }

            double mva = (double)js.ExecuteScript("return window.moving_diff + 0.00001");
            Logger.Info($"Calibration value retrieved from web browser: {mva}");
            BUSY_browser_response_setTimeout = Int32.Parse(Math.Round( mva * 1.1).ToString());
            Logger.Info($"Using a BUSY response timeout (in milliseconds) + 10% of : {BUSY_browser_response_setTimeout}");

        }

        private Boolean DoBUSYWait(Stopwatch timer)
        {
            Boolean isBrowserBusy;
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            String isBusy2_js = $@"
window.a = new Date();
// console.log('Created date a:' + window.a);
window.callback = arguments[arguments.length - 1];  // Selenium callback function
window.iidx=0;
window.queue_size = {BUSY_mv_average_queue_size};
window.response_timeout = {BUSY_browser_response_setTimeout};

setTimeout(doMovingAverage_BUSYwait, {BUSY_browser_setTimeout}, window.a, window.callback, {BUSY_browser_response_setTimeout}, {BUSY_mv_average_queue_size});
";

            if ((Boolean)js.ExecuteAsyncScript(isBusy2_js))
            {
                Logger.Info("Browser activity detected after: {0}", timer.Elapsed.ToString());
                isBrowserBusy = true;
            }
            else
            {
                Logger.Info("No BUSY browser activity detected: {0}", timer.Elapsed.ToString());
                isBrowserBusy = false;
            }

            return isBrowserBusy;
        }

        private Boolean DoNetworkWait(Stopwatch timer)
        {
            Boolean isNetworkBusy;
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;

            if ((Boolean)js.ExecuteScript("return window.isCallingAjax();"))
            {
                Logger.Info("Waiting on Network activity: {0}", timer.Elapsed.ToString());
                isNetworkBusy = true;

                //      Int64 op = (Int64)js.ExecuteScript("return window.requestCount;");
                //                 Console.WriteLine("Running Network count: {0}", op);
            }
            else
            {
                Logger.Info("No Network detected: {0}", timer.Elapsed.ToString());
                isNetworkBusy = false;
            }

            return isNetworkBusy;
        }

        private Boolean DoDOMWait(Stopwatch timer)
        {
            Boolean isDOMBusy;
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;

            if ((Boolean)js.ExecuteScript("return window.mutationObserver.haveNewMutations();"))
            {
                Logger.Info("Waiting on DOM activity: {0}", timer.Elapsed.ToString());
                isDOMBusy = true;

                //Int64 op = (Int64)js.ExecuteScript("return window.mutationObserver.getCount();");
                //                Logger.Info("Mutation Counter: {0}", op);

            }
            else
            {
                Logger.Info("No DOM activity detected: {0}", timer.Elapsed.ToString());
                isDOMBusy = false;

       //         Int64 op = (Int64)js.ExecuteScript("return window.mutationObserver.getCount();");
       //         Logger.Info("Mutation Counter: {0}", op);
            }

            return isDOMBusy;
        }

        private void WaitForPageLoad()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            Boolean browserBusy = true;
            Stopwatch timer = new Stopwatch();
            timer.Start();
            while (timer.Elapsed < new TimeSpan(0, 0, 0, 0, 30000) && browserBusy == true)
            {
                if (((string)js.ExecuteScript("return document.readyState")).ToString().Equals("complete"))
                {
                    Logger.Info("Page load after: {0}", timer.Elapsed.ToString());
                    browserBusy = false;
                    continue;
                }
                else
                {
                    Logger.Info("Page loading...");
                }
            }

            // recheck to prevent this from working too fast

            AddScreenshot($"{currentAction} Page load completed");

        }

        public void AddScreenshot(String desc)
        {
            // take screenshot image and save in list
            shotManager.AddShot(((ITakesScreenshot)driver).GetScreenshot(), desc);
        }

        public void LoadNetworkMonitor()
        {
            string netmonitor_js = @"
window.requestArray = [];
window.requestCount = 0;

XMLHttpRequest.prototype.realOpen = XMLHttpRequest.prototype.open;

window.newOpen = function(method, url, async) { 
 // debugger;
 //console.log('In newOpen()');
  this._method = method;
  this._url = url; 
//   console.log('Method: ' + method);
//   console.log('URL: ' + url);
  return XMLHttpRequest.prototype.realOpen.apply(this, arguments); 
};

// install new open function
XMLHttpRequest.prototype.open = window.newOpen;

XMLHttpRequest.prototype.realSend = XMLHttpRequest.prototype.send;

// here 'this' points to the XMLHttpRequest Object.
window.newSend = function(postData) {
//  debugger; 
  this.onreadystatechange = function() {
    if (this.readyState == XMLHttpRequest.DONE) {
  //      console.log('request ' + this._url);
 //       console.log('request body ' + postData);
 //       console.log('response ' + this.responseText);
        window.requestArray.push([this._url, this._method, postData, this.responseText, this.status, this.statusText]);
        window.requestCount--;
    }
  }
  this.realSend(postData);
  window.requestCount++;
};

// install new send function
XMLHttpRequest.prototype.send = window.newSend;

";
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            js.ExecuteScript(netmonitor_js);
        }

        public int GetNetworkMonitorQueueLength()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            int len = Int32.Parse(js.ExecuteScript("return window.requestArray.length").ToString());

            return len;
        }

        public ReadOnlyCollection<Object> GetNetworkMonitorByIndex(int idx)
        {
            ReadOnlyCollection<Object> result = null;
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            result = (ReadOnlyCollection<Object>)js.ExecuteScript($"return requestArray[{idx}];");

            return result;
        }

        public List<ReadOnlyCollection<Object>> GetNetworkMonitorByRegex(Regex rx, API_RESPONSE apiPart)
        {
            List<ReadOnlyCollection<Object>> filteredResult = new List<ReadOnlyCollection<Object>>();
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            int count = GetNetworkMonitorQueueLength();
            for(int idx=0; idx < count; idx++)
            {
                ReadOnlyCollection<Object> result = null;
                result = (ReadOnlyCollection<Object>)js.ExecuteScript($"return requestArray[{idx}];");
                string url = (string)result[(int)apiPart];

                if (rx.IsMatch(url))
                {
                    filteredResult.Add(result);
                }
            }
            
            return filteredResult;
        }

        public void LoadJSutils()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;

            // load xpath helper
            string xpath_js = @"

function getElementByXpath(path) {

  return document.evaluate(path, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;

}

var nativeEvents = {
    'submit': 'HTMLEvents',
    'keypress': 'KeyEvents',
    'click': 'MouseEvents',
    'dblclick': 'MouseEvents',
    'dragstart': 'MouseEvents',
    'dragend': 'MouseEvents',
    'wheel': 'WheelEvents',
}

window.events = [];

document.processEvent = function(event) {
    console.log(event);
    window.events.push(event);
}

for(var eventName in nativeEvents) {
    document.addEventListener(eventName, document.processEvent, true);
};

// attach to global element
document.getElementByXpath = getElementByXpath;

";
            js.ExecuteScript(xpath_js);
        }

        public bool IsJavacriptContextLoaded()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;

            // load xpath helper
            string ctc_check_js = @"

return typeof document.getElementByXpath != 'undefined';
";
            bool ret = (bool) js.ExecuteScript(ctc_check_js);

            return ret;
        }

        public void LoadJavascriptContext()
        {
            if (IsJavacriptContextLoaded() == false)
            {
                LoadJSutils();
                initJavascriptWaits();
                if (doNetworkWait)
                {
                    LoadNetworkMonitor();
                }
                WaitForAjax();

                Logger.Info("Javascript context loaded");
            }
            else
            {
                Logger.Warn("Javascript context is already present");
            }
        }

        public void SetFrame(string frame)
        {
            driver.SwitchTo().DefaultContent();

            // if using default context only
            if (frame != null)
            {
                // switch frame
                driver.SwitchTo().Frame(driver.FindElement(By.XPath($"//iframe[@title='{frame}']|//iframe[@class='{frame}']")));
            }

            // reload ajax fuctions for this context if required
            if (IsJavacriptContextLoaded() == false)
            {              
                doBrowserAction(d =>
                {
                    // keep empty - loading ajax
                    ;
                },
                actionDesc: "Ajax reload after Frame change",
                sync: SyncMethodEnum.NONE,
                isNewPageWithAjax: true);

                Logger.Info($"Reloading javscript context for frame '{(frame == null ? "null" : frame)}'");
            }
            
        }

        // perform click action robustly
        public void Click(By locator, Func<IWebDriver, bool> precondition = null, Func<IWebDriver, bool> postcondition = null)
        {
            bool met_precondition = true;
            bool met_postcondition = false;
            int attempts = 3;

            while(met_postcondition == false && attempts-- > 0)
            {
                // check pre
                if (precondition != null)
                {
                    if (met_precondition = precondition(driver) == true)
                        Logger.Info("PRECONDITION returned TRUE");
                    else
                        Logger.Error("PRECONDITION returned FALSE");
                    // cannot clink on link if pre-condition not met
                }

                // click

                // check post
                if (postcondition != null)
                {
                    if (met_postcondition = postcondition(driver) == true)
                        Logger.Info("POSTCONDITION returned TRUE");
                    else
                        Logger.Error("POSTCONDITION returned FALSE");
                }

            }

        }

        public void SetTestName_For_ScreenShots(string testnm)
        {
            shotManager.SetTestName(testnm);
        }

        public void Output_Screenshots()
        {
            // Output all screen shots
            shotManager.WriteScreenshots();
        }

        public void ScrollToBottomOfPage()
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            js.ExecuteScript("window.scrollTo(0, document.body.scrollHeight);");
        }

        public void ScrollPageDown(int amountY)
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)driver;
            js.ExecuteScript($"window.scrollBy(0, {amountY});");
        }
    }
}
