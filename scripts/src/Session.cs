using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SerialRf {
    // One owner per physical handle. A direction borrows only its RX or TX operation.
    // Canceling a completed lane must never cancel the opposite lane's pending I/O.
    public sealed class DirectionPort : IPort {
        readonly IDuplexPort port;
        readonly bool read;
        public DirectionPort(IDuplexPort port, bool read) { this.port=port;this.read=read; }
        public int Read(byte[] buffer) { if(!read)throw new InvalidOperationException("Write-only port lease.");return port.Read(buffer); }
        public void Write(byte[] buffer) { if(read)throw new InvalidOperationException("Read-only port lease.");port.Write(buffer); }
        public uint Errors(out uint input, out uint output) { return port.Errors(out input,out output); }
        public void Cancel() { if(read)port.CancelRead();else port.CancelWrite(); }
        public void Dispose() { } // Session releases the handle after both engines have joined.
    }

    public sealed class TestSession : IDisposable {
        public readonly TestConfig Config;
        public readonly TestEngine Forward, Reverse;
        public readonly ConcurrentQueue<string> Events=new ConcurrentQueue<string>();
        readonly Func<TestConfig,IDuplexPort[]> portFactory;
        IDuplexPort portA,portB;
        Thread coordinator;
        volatile bool aborted;
        public volatile bool Done;
        public TestSession(TestConfig config) : this(config,null) { }
        public TestSession(TestConfig config,Func<TestConfig,IDuplexPort[]> factory) {
            config.Validate();Config=config;portFactory=factory;
            if(config.Bidirectional) {
                Forward=new TestEngine(config.Direction(false),delegate(TestConfig c) { return new IPort[]{new DirectionPort(portA,false),new DirectionPort(portB,true)}; });
                Reverse=new TestEngine(config.Direction(true),delegate(TestConfig c) { return new IPort[]{new DirectionPort(portB,false),new DirectionPort(portA,true)}; });
            } else Forward=new TestEngine(config.Direction(false), factory);
        }
        public void Start() { coordinator=new Thread(Run) { IsBackground=true,Name="Serial test session" };coordinator.Start(); }
        public void Stop() { Forward.Stop();if(Reverse!=null)Reverse.Stop(); }
        public void Abort() { if(Done)return;aborted=true;Forward.Abort();if(Reverse!=null)Reverse.Abort(); }
        public bool Wait(int milliseconds) { return coordinator==null||coordinator.Join(milliseconds); }
        void DrainEvents(TestEngine engine,string direction) {
            string message;while(engine.Events.TryDequeue(out message))Events.Enqueue(direction+message);
        }
        void ClosePort(IDuplexPort port) {
            if(port==null)return;
            try { port.Dispose(); } catch(Exception ex) { string message="Port cleanup failed: "+ex.Message;Forward.Measure.Fail(message);if(Reverse!=null)Reverse.Measure.Fail(message);Events.Enqueue(message); }
        }
        void Run() {
            bool forwardStarted=false,reverseStarted=false;
            try {
                if(Config.Bidirectional) {
                    if(portFactory!=null) {
                        IDuplexPort[] pair=portFactory(Config);
                        if(pair==null||pair.Length!=2||pair[0]==null||pair[1]==null||ReferenceEquals(pair[0],pair[1]))throw new InvalidOperationException("Two distinct duplex endpoints are required.");
                        portA=pair[0];portB=pair[1];
                    } else {
                        portA=new NativeSerial(Config.TxPort,Config.Baud);
                        portB=new NativeSerial(Config.RxPort,Config.Baud);
                    }
                    Events.Enqueue("Both ports open once; independent A to B and B to A streams. Target load applies to each direction.");
                }
                if(aborted)throw new OperationCanceledException("Test canceled before transmission started.");
                Forward.Start();forwardStarted=true;if(Reverse!=null) { Reverse.Start();reverseStarted=true; }
                while(!Forward.Done||(Reverse!=null&&!Reverse.Done)) {
                    DrainEvents(Forward,Reverse==null?"":"A to B · ");
                    if(Reverse!=null) {
                        DrainEvents(Reverse,"B to A · ");
                        if(Forward.Measure.HasError||Reverse.Measure.HasError)Abort();
                    }
                    Thread.Sleep(20);
                }
            } catch(Exception ex) {
                if(!forwardStarted)Forward.FailBeforeStart(ex.Message);
                if(Reverse!=null&&!reverseStarted)Reverse.FailBeforeStart(ex.Message);
                if(forwardStarted||reverseStarted) { if(!Forward.Done)Forward.Measure.Fail(ex.Message);Abort(); }
                Events.Enqueue(ex.Message);
            } finally {
                // Cancellation is asynchronous: workers must exit before releasing shared handles.
                if(forwardStarted)Forward.Wait(Timeout.Infinite);if(reverseStarted)Reverse.Wait(Timeout.Infinite);
                DrainEvents(Forward,Reverse==null?"":"A to B · ");if(Reverse!=null)DrainEvents(Reverse,"B to A · ");
                ClosePort(portA);ClosePort(portB);Done=true;
            }
        }
        public void Dispose() {
            if(!Done) { Abort();if(!Wait(15000))return; }
            Forward.Dispose();if(Reverse!=null)Reverse.Dispose();
        }
    }
}
